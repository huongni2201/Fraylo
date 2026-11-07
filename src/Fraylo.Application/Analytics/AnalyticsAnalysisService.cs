using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fraylo.Application.Jobs;
using Fraylo.Application.Providers;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Analytics;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fraylo.Application.Analytics;

public sealed class AnalyticsAnalysisService(DbContextOptions<FrayloDbContext> options, ProviderRegistry providers,
    JobService jobs, IJobWakeupNotifier notifier)
{
    // This intent shares the source transaction. Sending can only start after commit.
    internal async Task<string> StageAsync(FrayloDbContext db, string platform, string accountId, string? pageId,
        string sourceId, string? requestId, CancellationToken ct)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\0", platform, accountId, pageId, sourceId, requestId))));
        if (await db.Set<AnalyticsAnalysisReport>().AnyAsync(r => r.Id == id, ct)) return id;
        var settings = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == SettingsService.KeyAnalyticsProvider || s.Key == SettingsService.KeyRevision)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var provider = settings.GetValueOrDefault(SettingsService.KeyAnalyticsProvider)?.Trim().ToLowerInvariant();
        db.Add(new AnalyticsAnalysisReport
        {
            Id = id, Platform = platform, AccountId = accountId, PageId = pageId, SourceId = sourceId,
            CreatedAtUtc = Now(), ProviderKey = string.IsNullOrWhiteSpace(provider) ? null : provider,
            SettingsRevision = long.TryParse(settings.GetValueOrDefault(SettingsService.KeyRevision), out var revision) ? revision : 1
        });
        return id;
    }

    internal async Task AfterCommitAsync(string? reportId)
    {
        if (reportId is null) return;
        try { await PrepareAsync(reportId, CancellationToken.None); }
        catch (Exception ex)
        {
            // The saved intent is still available for manual continuation.
            System.Diagnostics.Trace.TraceWarning("Analytics AI prepare interrupted: {0}", ex.GetType().Name);
        }
    }

    private async Task PrepareAsync(string reportId, CancellationToken ct)
    {
        await using var db = new FrayloDbContext(options);
        var report = await db.Set<AnalyticsAnalysisReport>().Include(r => r.Job).SingleAsync(r => r.Id == reportId, ct);
        if (report.Job is not null || report.PreparationReason is not null) return;

        var (input, reason) = await AnalyticsAnalysisInputBuilder.ReadAsync(db, report, ct);
        if (reason is null && report.ProviderKey is null) reason = "ProviderNotConfigured";
        var capability = reason is null && report.ProviderKey is not null && providers.TryGetAdapter(report.ProviderKey, out var adapter)
            ? await adapter!.GetCapabilityForTaskAsync(JobTaskType.AnalyticsAnalysis, ct) : null;
        if (reason is null && capability is not
            { AutomationSupported: true, VerificationStatus: ProviderVerificationStatus.Trial or ProviderVerificationStatus.Verified })
            reason = "CapabilityUnavailable";
        var prepared = reason is null && input is not null && capability is not null
            ? AnalyticsAnalysisInputBuilder.Fit(input, capability.MaxInputLength) : null;
        if (reason is null && prepared is null) reason = "InputTooLarge";

        if (prepared is null)
        {
            report.PreparationReason = reason;
            await db.SaveChangesAsync(ct);
            return;
        }
        var now = Now();
        report.InputJson = prepared.InputJson;
        report.InputHash = prepared.Hash;
        db.Jobs.Add(new Job
        {
            Id = report.Id, AnalyticsReportId = report.Id, ProviderKey = report.ProviderKey!,
            TaskType = JobTaskType.AnalyticsAnalysis, State = JobState.Queued,
            PromptSnapshotText = JsonSerializer.Serialize(new { templateId = "analytics-analysis-v1", renderedText = prepared.Prompt }),
            ConfigSnapshotJson = prepared.InputJson,
            CreatedAtUtc = now, UpdatedAtUtc = now, MaxAttempts = 1
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // The report ID is also the unique job ID. A concurrent prepare may already have committed.
            if (await db.Jobs.AsNoTracking().AnyAsync(j => j.Id == report.Id, ct)) return;
            throw;
        }
        notifier.NotifyWakeup(new JobWakeupSignal("analytics.analysis", report.Id));
    }

    public async Task HoldUnsentOnStartupAsync(CancellationToken ct)
    {
        await using var db = new FrayloDbContext(options);
        var now = Now();
        await db.Jobs.Where(j => j.TaskType == JobTaskType.AnalyticsAnalysis && j.State == JobState.Queued && j.SideEffectSubmitted == 0)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, JobState.AwaitingUser)
                .SetProperty(j => j.ReasonCode, "AnalysisRestarted").SetProperty(j => j.UpdatedAtUtc, now)
                .SetProperty(j => j.Revision, j => j.Revision + 1), ct);
    }

    public async Task<object> ListAsync(AnalyticsAnalysisRequest request, CancellationToken ct)
    {
        await using var db = new FrayloDbContext(options);
        await RequireScopeAsync(db, request, ct);
        if (request.Page is < 1 or > 10000) throw new ArgumentException("Trang lịch sử không hợp lệ.");
        var reports = await Scoped(db, request).AsNoTracking().Include(r => r.Job)
            .OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.Id).Skip((request.Page - 1) * 10).Take(11).ToListAsync(ct);
        var latestSourceId = await SourceIdAsync(db, request with { SourceId = null }, ct);
        var availableProviders = await db.ProviderAccounts.AsNoTracking().Where(a =>
                a.Enabled && a.Lifecycle == "Active" && a.Profile != null && a.Profile.SessionStatus == "Connected")
            .Select(a => a.ProviderKey).Distinct().ToListAsync(ct);
        return new { latestSourceId, reports = reports.Take(10).Select(r => View(r, availableProviders)).ToArray(), hasMore = reports.Count > 10 };
    }

    public async Task<object> GetAsync(AnalyticsAnalysisRequest request, CancellationToken ct)
    {
        await using var db = new FrayloDbContext(options);
        await RequireScopeAsync(db, request, ct);
        var report = await RequireReportAsync(db, request, ct);
        return View(report);
    }

    public async Task<object> StartAsync(AnalyticsAnalysisRequest request, bool retry, CancellationToken ct)
    {
        if (request.ClientRequestId is not { Length: 32 or 36 } || !Guid.TryParse(request.ClientRequestId, out var token))
            throw new ArgumentException("Mã yêu cầu phân tích không hợp lệ.");
        await using var db = new FrayloDbContext(options);
        await RequireScopeAsync(db, request, ct);
        var prior = retry ? await RequireReportAsync(db, request, ct) : null;
        if (prior?.Job is { State: JobState.AwaitingUser, SideEffectSubmitted: 0 })
            return await jobs.ResumeAsync(prior.Job.Id, ct);
        if (prior?.Job is { State: JobState.Queued or JobState.Reserved or JobState.Running or JobState.AwaitingUser })
            throw new InvalidOperationException("Lượt phân tích đang chạy hoặc chưa xác định kết quả gửi. Hãy kiểm tra hoặc hủy lượt đó trước.");
        if (prior is { Job: null, PreparationReason: null })
        {
            await PrepareAsync(prior.Id, ct);
            return new { reportId = prior.Id };
        }

        var sourceId = await SourceIdAsync(db, request with { SourceId = prior?.SourceId ?? request.SourceId }, ct)
            ?? throw new InvalidOperationException("Chưa tìm thấy lần cập nhật số liệu này.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await Scoped(db, request).Where(r => r.SourceId == sourceId &&
                (!retry || r.Job == null && r.PreparationReason == null ||
                    r.Job != null && (r.Job.State == JobState.Queued || r.Job.State == JobState.Reserved ||
                        r.Job.State == JobState.Running || r.Job.State == JobState.AwaitingUser)))
            .OrderByDescending(r => r.CreatedAtUtc).Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return new { reportId = existing };
        var id = await StageAsync(db, request.Platform, request.AccountId, request.PageId, sourceId, token.ToString("N"), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PrepareAsync(id, ct);
        return new { reportId = id };
    }

    public async Task<object> CancelAsync(AnalyticsAnalysisRequest request, CancellationToken ct)
    {
        await using var db = new FrayloDbContext(options);
        await RequireScopeAsync(db, request, ct);
        var report = await RequireReportAsync(db, request, ct);
        if (report.Job is null) throw new InvalidOperationException("Lượt phân tích chưa vào hàng đợi.");
        return await jobs.CancelAsync(report.Job.Id, ct);
    }

    private static IQueryable<AnalyticsAnalysisReport> Scoped(FrayloDbContext db, AnalyticsAnalysisRequest r) =>
        db.Set<AnalyticsAnalysisReport>().Where(x => x.Platform == r.Platform && x.AccountId == r.AccountId && x.PageId == r.PageId);

    private static async Task<AnalyticsAnalysisReport> RequireReportAsync(FrayloDbContext db, AnalyticsAnalysisRequest r, CancellationToken ct) =>
        await Scoped(db, r).AsNoTracking().Include(x => x.Job).SingleOrDefaultAsync(x => x.Id == r.ReportId, ct)
        ?? throw new KeyNotFoundException("Không tìm thấy báo cáo trong tài khoản hoặc Fanpage đã chọn.");

    private static async Task RequireScopeAsync(FrayloDbContext db, AnalyticsAnalysisRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.AccountId) ||
            r.Platform == "tiktok" && r.PageId is not null ||
            r.Platform == "facebook" && string.IsNullOrWhiteSpace(r.PageId) ||
            r.Platform is not ("tiktok" or "facebook")) throw new ArgumentException("Chọn tài khoản hoặc Fanpage hợp lệ.");
        var kind = r.Platform == "tiktok" ? "TikTok" : "Facebook";
        if (!await db.BrowserProfiles.AnyAsync(p => p.Id == r.AccountId && p.Kind == kind && p.Lifecycle == "Active", ct) ||
            r.Platform == "facebook" && !await db.FacebookAnalyticsPages.AnyAsync(p => p.Id == r.PageId && p.ProfileId == r.AccountId, ct))
            throw new KeyNotFoundException("Tài khoản hoặc Fanpage không còn khả dụng.");
    }

    private static Task<string?> SourceIdAsync(FrayloDbContext db, AnalyticsAnalysisRequest r, CancellationToken ct) =>
        r.Platform == "tiktok"
            ? db.AnalyticsSnapshots.Where(s => s.AccountId == r.AccountId && (r.SourceId == null || s.Id == r.SourceId))
                .OrderByDescending(s => s.CreatedAtUtc).ThenBy(s => s.Id).Select(s => (string?)s.Id).FirstOrDefaultAsync(ct)
            : db.FacebookAnalyticsCaptures.Where(c => c.PageId == r.PageId && (c.Status == "Complete" || c.Status == "Partial") &&
                (r.SourceId == null || c.Id == r.SourceId)).OrderByDescending(c => c.StartedAtUtc).ThenBy(c => c.Id)
                .Select(c => (string?)c.Id).FirstOrDefaultAsync(ct);

    private static object View(AnalyticsAnalysisReport r, List<string>? connectedProviders = null)
    {
        var input = r.InputJson is null ? null : JsonSerializer.Deserialize<AnalyticsAnalysisInputBuilder.Input>(r.InputJson, AnalyticsAnalysisInputBuilder.Json);
        var result = r.Job?.State == JobState.Succeeded ? AnalyticsAnalysisResultParser.Parse(r.Job.ResultText, r.InputJson) : null;
        var state = r.Job?.State ?? (r.PreparationReason is null ? "PendingEnqueue" : "Skipped");
        var reason = r.Job?.ReasonCode ?? r.PreparationReason;
        if (state == JobState.Queued && connectedProviders is not null && !connectedProviders.Contains(r.ProviderKey ?? ""))
            reason = "NoConnectedProvider";
        if (state == JobState.Succeeded && result is null)
        {
            state = JobState.Failed;
            reason = AnalyticsAnalysisResultParser.InvalidResult;
        }
        return new { r.Id, r.SourceId, r.CreatedAtUtc, r.ProviderKey, state, reason, input, result };
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
