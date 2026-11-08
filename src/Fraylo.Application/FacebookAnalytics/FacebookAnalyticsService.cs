using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Analytics;
using Fraylo.Browser.Models;
using Fraylo.Browser.Publishing;
using Fraylo.Contracts.FacebookAnalytics;
using Fraylo.Application.Analytics;
using Fraylo.Application.Facebook;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Fraylo.Application.FacebookAnalytics;

public sealed class FacebookAnalyticsService(DbContextOptions<FrayloDbContext> options, IProfileRegistry profiles,
    IBrowserProcessController controller, IFacebookInsightsBrowser browser, AnalyticsAnalysisService? analysis = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Operation> _operations = new();
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Owner = Environment.ProcessId + ":" + Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    public static readonly string[] CountMetrics = ["views", "reactions", "comments", "shares"];
    private sealed class Operation(string account, string page, CancellationTokenSource cancellation, string activityLogJson)
    {
        public string Account { get; } = account;
        public string Page { get; } = page;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Work { get; set; } = Task.CompletedTask;
        public string Message { get; set; } = "Đang kiểm tra phiên";
        public string ActivityLogJson { get; set; } = activityLogJson;
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        // Không phục hồi giữa commit capture và đăng ký worker của StartAsync.
        if (!await _start.WaitAsync(0, ct)) return;
        try { await RecoverInterruptedCoreAsync(ct); }
        finally { _start.Release(); }
    }

    private async Task RecoverInterruptedCoreAsync(CancellationToken ct)
    {
        await using var db = new FrayloDbContext(options);
        var pending = await db.FacebookAnalyticsCaptures.Where(c => c.Status == "Running").ToListAsync(ct);
        foreach (var row in pending)
        {
            if (OwnerAlive(row.Owner) && (row.Owner != Owner || _operations.ContainsKey(row.Id))) continue;
            var reason = row.Owner == Owner ? "save_failed" : "interrupted";
            row.Status = "Failed";
            row.Message = reason == "save_failed"
                ? "save_failed: Chưa lưu được kết quả của lượt cập nhật. Dữ liệu trước được giữ nguyên; bạn có thể cập nhật lại."
                : "interrupted: Lượt cập nhật trước đã dừng; ứng dụng không tự chạy lại.";
            row.FinishedAtUtc = Now();
            row.ActivityLogJson = FacebookActivityLog.Append(row.ActivityLogJson, row.FinishedAtUtc, "Failed", row.Message, reason);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<FacebookAnalyticsCatalog> CatalogAsync(CancellationToken ct = default)
    {
        await using var db = new FrayloDbContext(options);
        var accounts = await db.BrowserProfiles.AsNoTracking().Where(p => p.Kind == "Facebook" && p.Lifecycle == "Active" &&
            db.ProviderAccounts.Any(a => a.ProfileId == p.Id && a.ProviderKey == "facebook" && a.Lifecycle == "Active"))
            .Select(p => new FacebookAnalyticsAccount(p.Id, p.DisplayName ?? "Facebook", p.SessionStatus,
                db.ProviderAccounts.Any(a => a.ProfileId == p.Id && a.ProviderKey == "facebook" && a.Lifecycle == "Active" && a.Enabled))).ToListAsync(ct);
        var ids = accounts.Select(a => a.AccountId).ToArray();
        var pages = await db.FacebookAnalyticsPages.AsNoTracking().Where(p => ids.Contains(p.ProfileId)).OrderBy(p => p.Name).ToListAsync(ct);
        return new(accounts, pages.Select(PageDto).ToArray());
    }

    public async Task<FacebookAnalyticsCatalog> SyncPagesAsync(FacebookAnalyticsRequest request, CancellationToken ct = default)
    {
        await _start.WaitAsync(ct);
        try
        {
            var profile = await ConnectedAsync(request.AccountId, ct);
            if (_operations.Values.Any(o => o.Account == request.AccountId)) throw new InvalidOperationException("Tài khoản đang cập nhật số liệu; chờ hoàn tất trước khi tải Fanpage.");
            await CheckBudgetAsync(profile.ProfileId, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var found = await browser.DiscoverAsync(profile.ProfileId, profile.AccountIdentity!, timeout.Token);
            await using var db = new FrayloDbContext(options);
            foreach (var item in found)
            {
                if (!FacebookInsightsBrowser.ValidId(item.FacebookPageId)) throw new InvalidOperationException("Facebook trả ID Fanpage không hợp lệ.");
                var page = await db.FacebookAnalyticsPages.SingleOrDefaultAsync(p => p.ProfileId == request.AccountId && p.FacebookPageId == item.FacebookPageId, ct);
                if (page == null) { page = new() { Id = Guid.NewGuid().ToString("N"), ProfileId = request.AccountId, FacebookPageId = item.FacebookPageId }; db.Add(page); }
                page.Name = item.Name; page.Url = item.Url; page.VerifiedAtUtc = Now();
            }
            // Discovery có thể chưa tải hết; không xóa các Page cũ chỉ vì không xuất hiện trong lần đọc này.
            await db.SaveChangesAsync(ct);
            return await CatalogAsync(ct);
        }
        catch (FacebookNeedsUserException ex) { await InvalidateAsync(request.AccountId, ex.ReasonCode); throw; }
        finally { _start.Release(); }
    }

    public async Task<FacebookCaptureDto> StartAsync(FacebookAnalyticsRequest request, CancellationToken ct = default)
    {
        if (!Guid.TryParse(request.OperationId, out var operationGuid)) throw new ArgumentException("Cần mã lượt cập nhật hợp lệ.");
        var operationId = operationGuid.ToString("N");
        await _start.WaitAsync(ct);
        try
        {
            await using var db = new FrayloDbContext(options);
            var page = await BindingAsync(db, request, ct);
            var existing = await db.FacebookAnalyticsCaptures.AsNoTracking().SingleOrDefaultAsync(c => c.Id == operationId, ct);
            if (existing != null)
            {
                if (existing.PageId != page.Id) throw new ArgumentException("Mã lượt cập nhật đã dùng cho Fanpage khác.");
                return CaptureDto(existing);
            }
            var profile = await ConnectedAsync(request.AccountId, ct);
            await CheckBudgetAsync(profile.ProfileId, ct);
            if (_operations.Values.Any(o => o.Account == request.AccountId)) throw new InvalidOperationException("Tài khoản đang có lượt cập nhật. Hãy chờ hoặc hủy lượt đó.");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.FacebookPostTasks.AnyAsync(t => t.ProfileId == request.AccountId && (t.State == "Preparing" || t.State == "Submitting"), ct) ||
                await db.FacebookAnalyticsCaptures.AnyAsync(c => c.Status == "Running" && db.FacebookAnalyticsPages.Any(p => p.Id == c.PageId && p.ProfileId == request.AccountId), ct))
                throw new InvalidOperationException("Tài khoản Facebook đang có tác vụ; hãy chờ hoàn tất.");
            var capture = new FacebookAnalyticsCapture { Id = operationId, PageId = page.Id, StartedAtUtc = Now(), Owner = Owner, Message = "Đang kiểm tra phiên" };
            capture.ActivityLogJson = FacebookActivityLog.Append("[]", capture.StartedAtUtc, "checking_session", capture.Message);
            db.Add(capture); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            cancellation.CancelAfter(TimeSpan.FromMinutes(5));
            var operation = new Operation(request.AccountId, page.Id, cancellation, capture.ActivityLogJson);
            _operations[operationId] = operation;
            operation.Work = Task.Run(() => RunAsync(capture.Id, page, profile, operation));
            return CaptureDto(capture);
        }
        finally { _start.Release(); }
    }

    private async Task RunAsync(string id, FacebookAnalyticsPage page, BrowserProfileInfo profile, Operation operation)
    {
        var status = "Failed";
        var message = "Chưa hoàn tất cập nhật.";
        string? reason = null;
        IReadOnlyList<FacebookContentDto> records = [];
        try
        {
            var result = await browser.ReadAsync(profile.ProfileId, profile.AccountIdentity!, page.FacebookPageId, page.Name,
                value =>
                {
                    if (value == operation.Message) return;
                    operation.ActivityLogJson = FacebookActivityLog.Append(operation.ActivityLogJson, Now(), "reading_insights", value);
                    operation.Message = value;
                }, operation.Cancellation.Token);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            if (result.Content.Count > FacebookInsightsBrowser.MaxContent || result.Content.Any(r => !FacebookInsightsBrowser.ValidId(r.ContentId)) ||
                result.Content.Select(r => r.ContentId).Distinct().Count() != result.Content.Count) throw new InvalidOperationException("Kết quả Facebook không hợp lệ hoặc trùng ID nội dung.");
            records = result.Content;
            status = result.Complete ? "Complete" : "Partial"; message = result.Message;
        }
        catch (OperationCanceledException) { status = "Canceled"; reason = "canceled_or_timed_out"; message = "Đã dừng cập nhật (hủy hoặc hết thời gian 5 phút). Dữ liệu trước được giữ nguyên."; }
        catch (FacebookNeedsUserException ex) { reason = ex.ReasonCode; message = ex.Message; await InvalidateAsync(profile.ProfileId, ex.ReasonCode); }
        catch (Exception ex)
        {
            (reason, message) = ex switch
            {
                TimeoutException => ("browser_timeout", "Hết thời gian chờ Facebook tải số liệu. Kiểm tra kết nối và phiên trong Cài đặt."),
                PlaywrightException => ("browser_error", "Facebook chưa tải được dữ liệu hoặc giao diện thay đổi. Kiểm tra kết nối và phiên trong Cài đặt."),
                IOException => ("profile_unavailable", "Profile Facebook đang bị khóa hoặc chưa truy cập được. Hãy chờ thao tác khác hoàn tất rồi thử lại."),
                InvalidOperationException => ("insights_unavailable", ex.Message),
                _ => ("insights_failed", "Không thể cập nhật số liệu Fanpage. Dữ liệu đã lưu trước đó được giữ nguyên.")
            };
        }
        var finishedAt = Now();
        var logMessage = reason == "insights_unavailable" ? "Không xác minh được giao diện hoặc dữ liệu Fanpage." : message;
        var log = FacebookActivityLog.Append(operation.ActivityLogJson, finishedAt, status, logMessage, reason);
        string? reportId = null;
        try
        {
            // Chỉ thử lưu lại khi SQLite bận; không chạy browser lần thứ hai.
            for (var attempt = 0; ; attempt++)
            {
                try { reportId = await FinishAsync(id, page, status, message, finishedAt, log, records); break; }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 2) { await Task.Delay(200 * (attempt + 1)); }
            }
        }
        catch (Exception) { operation.Message = "Chưa lưu được kết quả vào cơ sở dữ liệu; không báo cập nhật thành công."; }
        finally
        {
            _operations.TryRemove(id, out _);
            operation.Cancellation.Dispose();
        }
        if (analysis is not null) await analysis.AfterCommitAsync(reportId);
    }

    private async Task<string?> FinishAsync(string id, FacebookAnalyticsPage page, string status, string message,
        string finishedAt, string log, IReadOnlyList<FacebookContentDto> records)
    {
        await using var db = new FrayloDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var changed = await db.FacebookAnalyticsCaptures.Where(c => c.Id == id && c.Status == "Running" && c.Owner == Owner)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status).SetProperty(c => c.Message, message)
                .SetProperty(c => c.RecordCount, records.Count).SetProperty(c => c.FinishedAtUtc, finishedAt).SetProperty(c => c.ActivityLogJson, log));
        if (changed != 1) return null;
        foreach (var record in records) db.FacebookContentMetricSnapshots.Add(new() { Id = Guid.NewGuid().ToString("N"), CaptureId = id, ContentId = record.ContentId, DataJson = JsonSerializer.Serialize(record, Json) });
        var reportId = analysis is not null && status is ("Complete" or "Partial")
            ? await analysis.StageAsync(db, "facebook", page.ProfileId, page.Id, id, null, CancellationToken.None) : null;
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return reportId;
    }

    public async Task CancelAsync(FacebookAnalyticsRequest request, CancellationToken ct = default)
    {
        await using var db = new FrayloDbContext(options);
        var page = await BindingAsync(db, request, ct);
        if (!Guid.TryParse(request.OperationId, out var guid)) throw new ArgumentException("Mã lượt cập nhật không hợp lệ.");
        var id = guid.ToString("N");
        var capture = await db.FacebookAnalyticsCaptures.SingleOrDefaultAsync(c => c.Id == id && c.PageId == page.Id, ct) ?? throw new KeyNotFoundException("Không thấy lượt cập nhật của Fanpage này.");
        if (capture.Status == "Running" && _operations.TryGetValue(id, out var operation))
        { try { operation.Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        else if (capture.Status == "Running") await RecoverInterruptedAsync(ct);
    }

    public async Task<FacebookAnalyticsDashboard> DashboardAsync(FacebookAnalyticsRequest request, CancellationToken ct = default)
    {
        ValidateQuery(request);
        await RecoverInterruptedAsync(ct);
        await using var db = new FrayloDbContext(options);
        var page = await BindingAsync(db, request, ct);
        var captures = await db.FacebookAnalyticsCaptures.AsNoTracking().Where(c => c.PageId == page.Id).OrderByDescending(c => c.StartedAtUtc).Take(20).ToListAsync(ct);
        var selected = await db.FacebookAnalyticsCaptures.AsNoTracking().Where(c => c.PageId == page.Id && (c.Status == "Complete" || c.Status == "Partial"))
            .OrderByDescending(c => c.StartedAtUtc).FirstOrDefaultAsync(ct);
        var content = selected == null ? [] : (await db.FacebookContentMetricSnapshots.AsNoTracking().Where(r => r.CaptureId == selected.Id).ToListAsync(ct)).Select(Deserialize).ToArray();
        var filtered = Filter(content, request).ToArray();
        var totals = CountMetrics.ToDictionary(k => k, k =>
        {
            var values = filtered.Select(r => r.Metrics.GetValueOrDefault(k)).Where(m => m?.Value != null).Cast<FacebookMetric>().ToArray();
            return new FacebookMetricTotal(values.Length == 0 ? null : values.Sum(m => m.Value!.Value), values.Length, filtered.Length, values.Any(m => m.Approximate));
        });
        var sorted = request.Sort == "published" ? filtered.OrderByDescending(r => r.PublishedDate ?? "").ThenBy(r => r.ContentId) :
            filtered.OrderBy(r => r.Metrics.GetValueOrDefault(request.Sort)?.Value == null).ThenByDescending(r => r.Metrics.GetValueOrDefault(request.Sort)?.Value).ThenBy(r => r.ContentId);
        var latest = captures.FirstOrDefault();
        var latestDto = latest == null ? null : CaptureDto(latest);
        if (latestDto is { Status: "Running" })
        {
            latestDto = _operations.TryGetValue(latestDto.Id, out var current)
                ? latestDto with { Message = current.Message, ActivityLog = FacebookActivityLog.Read(current.ActivityLogJson) }
                : latestDto with { Message = "Lượt cập nhật đã gián đoạn; đóng và mở lại Fraylo để đối soát." };
        }
        return new(selected == null ? null : CaptureDto(selected), latestDto, sorted.Skip((request.Page - 1) * 20).Take(20).ToArray(),
            filtered.Length, request.Page, totals, captures.Select(CaptureDto).ToArray());
    }

    public async Task<IReadOnlyList<FacebookContentObservation>> HistoryAsync(FacebookAnalyticsRequest request, CancellationToken ct = default)
    {
        if (!FacebookInsightsBrowser.ValidId(request.ContentId)) throw new ArgumentException("ID nội dung không hợp lệ.");
        await using var db = new FrayloDbContext(options);
        var page = await BindingAsync(db, request, ct);
        var rows = await db.FacebookContentMetricSnapshots.AsNoTracking().Where(r => r.ContentId == request.ContentId)
            .Join(db.FacebookAnalyticsCaptures.Where(c => c.PageId == page.Id && (c.Status == "Complete" || c.Status == "Partial")), r => r.CaptureId, c => c.Id, (r, c) => new { Row = r, c.StartedAtUtc })
            .OrderByDescending(x => x.StartedAtUtc).Take(25).ToListAsync(ct);
        return rows.Select(x => new FacebookContentObservation(x.StartedAtUtc, x.Row.CaptureId, Deserialize(x.Row))).ToArray();
    }

    private static IEnumerable<FacebookContentDto> Filter(IEnumerable<FacebookContentDto> content, FacebookAnalyticsRequest r) => content
        .Where(c => string.IsNullOrEmpty(r.Kind) || c.Kind == r.Kind)
        .Where(c => string.IsNullOrWhiteSpace(r.Search) || c.Title.Contains(r.Search, StringComparison.OrdinalIgnoreCase))
        .Where(c => r.Days == 0 || (DateOnly.TryParseExact(c.PublishedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
            day >= DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Pacific Standard Time")).AddDays(-r.Days)));
    private static void ValidateQuery(FacebookAnalyticsRequest request)
    {
        if (request.Page is < 1 or > 100000 || request.Search?.Length > 200 || request.Days is not (0 or 7 or 30 or 90) ||
            request.Kind is not (null or "" or "Post" or "Reel") || !CountMetrics.Contains(request.Sort) && request.Sort != "published") throw new ArgumentException("Bộ lọc Analytics không hợp lệ.");
    }
    private static async Task<FacebookAnalyticsPage> BindingAsync(FrayloDbContext db, FacebookAnalyticsRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.AccountId) || string.IsNullOrWhiteSpace(r.PageId)) throw new ArgumentException("Chọn tài khoản và Fanpage.");
        return await db.FacebookAnalyticsPages.AsNoTracking().SingleOrDefaultAsync(p => p.Id == r.PageId && p.ProfileId == r.AccountId &&
            db.BrowserProfiles.Any(b => b.Id == p.ProfileId && b.Kind == "Facebook" && b.Lifecycle == "Active"), ct) ?? throw new KeyNotFoundException("Fanpage không thuộc tài khoản đã chọn.");
    }
    private async Task<BrowserProfileInfo> ConnectedAsync(string account, CancellationToken ct)
    {
        var profile = await profiles.GetProfileAsync(account, ct);
        await using var db = new FrayloDbContext(options);
        if (profile == null || profile.Kind != "Facebook" || profile.Lifecycle != "Active" || profile.SessionStatus != "Connected" || string.IsNullOrWhiteSpace(profile.AccountIdentity) ||
            !await db.ProviderAccounts.AnyAsync(a => a.ProfileId == account && a.ProviderKey == "facebook" && a.Lifecycle == "Active" && a.Enabled, ct))
            throw new InvalidOperationException("Hãy bật và đăng nhập tài khoản Facebook trong Cài đặt trước khi cập nhật.");
        return profile;
    }
    private async Task CheckBudgetAsync(string profileId, CancellationToken ct)
    {
        await RecoverInterruptedCoreAsync(ct);
        if (controller.IsRunning(profileId) || controller.IsLoginWindowOpen(profileId)) throw new InvalidOperationException("Profile Facebook đang có thao tác khác; hãy chờ hoàn tất.");
        await using var db = new FrayloDbContext(options);
        var all = await profiles.ListProfilesAsync(ct);
        var raw = await db.AppSettings.Where(s => s.Key == "scheduler.max_open_browsers").Select(s => s.Value).SingleOrDefaultAsync(ct);
        var limit = int.TryParse(raw, out var number) && number > 0 ? number : 3;
        var occupied = await db.ActiveSlotOccupancies.Join(db.ProviderAccounts, o => o.ProviderAccountId, a => a.Id, (o, a) => a.ProfileId).ToListAsync(ct);
        var uncertain = await db.Jobs.Where(j => j.State == "AwaitingUser").Join(db.ExecutionBindings, j => j.Id, b => b.JobId, (j, b) => b.ProfileId).ToListAsync(ct);
        var open = occupied.Concat(uncertain).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var analytics = await db.FacebookAnalyticsCaptures.Where(c => c.Status == "Running")
            .Join(db.FacebookAnalyticsPages, c => c.PageId, p => p.Id, (c, p) => p.ProfileId).Distinct().ToListAsync(ct);
        open.UnionWith(analytics);
        foreach (var item in all.Where(p => controller.IsRunning(p.ProfileId) || controller.IsLoginWindowOpen(p.ProfileId) || p.ActiveJobCount > 0 || p.OperationalState is "Running" or "Idle" or "Quiescing")) open.Add(item.ProfileId);
        if (open.Count >= limit) throw new InvalidOperationException("Đang hết chỗ mở trình duyệt. Chờ tác vụ khác hoàn tất rồi cập nhật lại.");
    }
    private async Task InvalidateAsync(string profile, string reason)
    {
        try { await profiles.UpdateProfileStateAsync(profile, sessionStatus: "Unverified", reasonCode: reason); }
        catch (Exception) { Trace.TraceError("Facebook Analytics: chưa lưu được trạng thái phiên."); }
    }
    private static bool OwnerAlive(string owner)
    {
        var parts = owner.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || pid <= 0 || !long.TryParse(parts[1], out var ticks)) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static FacebookContentDto Deserialize(FacebookContentMetricSnapshot r) => JsonSerializer.Deserialize<FacebookContentDto>(r.DataJson, Json) ?? throw new InvalidOperationException("Dữ liệu Analytics đã lưu không hợp lệ.");
    private static FacebookAnalyticsPageDto PageDto(FacebookAnalyticsPage p) => new(p.Id, p.ProfileId, p.FacebookPageId, p.Name, p.Url);
    private static FacebookCaptureDto CaptureDto(FacebookAnalyticsCapture c) => new(c.Id, c.PageId, c.Status, c.StartedAtUtc, c.FinishedAtUtc, c.Message, c.RecordCount, c.Source, c.Scope, FacebookActivityLog.Read(c.ActivityLogJson));
    public async ValueTask DisposeAsync()
    {
        await _start.WaitAsync();
        Task[] pending;
        try
        {
            _shutdown.Cancel();
            pending = _operations.Values.Select(o => o.Work).ToArray();
        }
        finally { _start.Release(); }
        await Task.WhenAll(pending);
        _shutdown.Dispose();
    }
}
