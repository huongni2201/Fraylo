using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Browser.Publishing;
using Fraylo.Contracts.Facebook;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Application.Facebook;

/// <summary>Hàng đợi Facebook bền vững. Một bước CAS trước click; không tự gửi lại sau lỗi hoặc khởi động lại.</summary>
public sealed class FacebookPublishingService(
    DbContextOptions<FrayloDbContext> dbOptions,
    StorageRootRegistry roots,
    IProfileRegistry profiles,
    IFacebookGroupBrowser browser,
    IBrowserProcessController controller,
    TimeProvider? timeProvider = null, IFacebookPoster? personalPoster = null, FacebookContentRewriteService? rewrite = null)
{
    private sealed record TaskOutcome(string TaskId, string State, string Reason, string Message);
    private readonly Dictionary<string, TaskOutcome> _pendingOutcomes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly string _owner = $"{Environment.ProcessId}:{Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks}:{Guid.NewGuid():N}";
    private readonly string _startedAt = (timeProvider ?? TimeProvider.System).GetUtcNow().ToString("O");
    private static readonly string[] ActiveStates = ["Queued", "Preparing", "Submitting", "NeedsUser"];
    private const int MaxDrafts = 50;
    private DateTimeOffset UtcNow => (timeProvider ?? TimeProvider.System).GetUtcNow();
    private string Now() => UtcNow.ToString("O");

    public async Task<FacebookListResponse> ListAsync(CancellationToken ct = default)
    {
        await using var db = new FrayloDbContext(dbOptions);
        var accounts = await db.BrowserProfiles.AsNoTracking().Where(p => p.Kind == "Facebook" && p.Lifecycle == "Active" && db.ProviderAccounts.Any(a => a.ProfileId == p.Id && a.ProviderKey == "facebook" && a.Lifecycle == "Active"))
            .OrderBy(p => p.DisplayName).Select(p => new FacebookAccountDto(p.Id, p.DisplayName ?? p.AccountIdentity ?? "Facebook", p.AccountIdentity, p.SessionStatus, db.ProviderAccounts.Any(a => a.ProfileId == p.Id && a.ProviderKey == "facebook" && a.Lifecycle == "Active" && a.Enabled))).ToListAsync(ct);
        var groups = await db.FacebookTargets.AsNoTracking().Where(t => t.Lifecycle == "Active").OrderBy(t => t.Name).ToListAsync(ct);
        var active = await db.FacebookPostTasks.AsNoTracking().Where(t => ActiveStates.Contains(t.State)).OrderBy(t => t.CreatedAtUtc).ToListAsync(ct);
        var history = await db.FacebookPostTasks.AsNoTracking().Where(t => !ActiveStates.Contains(t.State)).OrderByDescending(t => t.UpdatedAtUtc).ToListAsync(ct);
        var drafts = await db.FacebookDrafts.AsNoTracking().OrderByDescending(d => d.UpdatedAtUtc).Take(MaxDrafts).ToListAsync(ct);
        var tasks = await TaskDtosAsync(db, active.Concat(history).OrderBy(t => t.SortOrder).ThenBy(t => t.Id), ct);
        var batches = await db.FacebookPostBatches.AsNoTracking().OrderByDescending(b => b.CreatedAtUtc).ToListAsync(ct);
        var posts = new List<FacebookPostDto>();
        foreach (var batch in batches)
            posts.Add(new(batch.Id, batch.Name, batch.ProfileId, batch.Paused, batch.Revision, batch.CreatedAtUtc,
                tasks.Where(t => t.BatchId == batch.Id).ToArray(), batch.OriginalTextSnapshot,
                batch.RequiresRewrite ? await FacebookContentRewriteService.ViewsAsync(db, batch.Id, ct) : null));
        var rewriteProvider = await db.AppSettings.AsNoTracking().Where(s => s.Key == Settings.SettingsService.KeyFacebookRewriteProvider).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return new(accounts, groups.Select(GroupDto).ToArray(), tasks, drafts.Select(DraftDto).ToArray(), posts, rewriteProvider);
    }

    public async Task<FacebookDraftDto> SaveDraftAsync(FacebookSaveDraftRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 80) throw new ArgumentException("Tên nháp cần từ 1 đến 80 ký tự.");
        if (string.IsNullOrWhiteSpace(request.AccountId)) throw new ArgumentException("Cần chọn tài khoản cho nháp.");
        if (request.Text == null || request.Text.EnumerateRunes().Count() > 10000) throw new ArgumentException("Nội dung nháp tối đa 10.000 ký tự.");
        if (request.TargetIds == null || request.TargetIds.Count > 5000 || request.TargetIds.Any(string.IsNullOrWhiteSpace) || request.TargetIds.Distinct(StringComparer.Ordinal).Count() != request.TargetIds.Count)
            throw new ArgumentException("Danh sách nhóm của nháp không hợp lệ.");
        if (request.ImageAssetIds == null || request.ImageAssetIds.Count > 10 || request.ImageAssetIds.Any(string.IsNullOrWhiteSpace) || request.ImageAssetIds.Distinct(StringComparer.Ordinal).Count() != request.ImageAssetIds.Count)
            throw new ArgumentException("Nháp chứa tối đa 10 ảnh khác nhau.");
        await using var db = new FrayloDbContext(dbOptions);
        var now = Now();
        FacebookDraft draft;
        if (string.IsNullOrWhiteSpace(request.DraftId))
        {
            if (await db.FacebookDrafts.CountAsync(ct) >= MaxDrafts) throw new InvalidOperationException($"Tối đa {MaxDrafts} nháp; hãy xóa bớt nháp cũ.");
            draft = new() { Id = Guid.NewGuid().ToString("N"), CreatedAtUtc = now };
            db.FacebookDrafts.Add(draft);
        }
        else
        {
            draft = await db.FacebookDrafts.SingleOrDefaultAsync(d => d.Id == request.DraftId, ct) ?? throw new KeyNotFoundException("Không tìm thấy nháp.");
            draft.Revision++;
        }
        draft.Name = name; draft.ProfileId = request.AccountId; draft.Text = request.Text;
        draft.TargetIdsJson = JsonSerializer.Serialize(request.TargetIds); draft.ImageAssetIdsJson = JsonSerializer.Serialize(request.ImageAssetIds);
        draft.DueAtUtc = ParseDueAt(request.DueAtUtc)?.ToString("O");
        draft.RewriteContent = request.RewriteContent;
        draft.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return DraftDto(draft);
    }

    public async Task DeleteDraftAsync(FacebookDraftRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = new FrayloDbContext(dbOptions);
        if (await db.FacebookDrafts.Where(d => d.Id == request.DraftId).ExecuteDeleteAsync(ct) != 1) throw new KeyNotFoundException("Không tìm thấy nháp.");
    }

    public async Task<FacebookSyncResponse> SyncGroupsAsync(FacebookSyncRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = await ConnectedProfileAsync(request.AccountId, ct);
        if (!await _dispatch.WaitAsync(0, ct)) throw new InvalidOperationException("Đang xử lý tác vụ Facebook; hãy chờ hoàn tất trước khi tải nhóm.");
        try
        {
            if (controller.IsLoginWindowOpen(profile.ProfileId)) throw new InvalidOperationException("Hãy đóng cửa sổ đăng nhập Facebook trước khi tải nhóm.");
            await using (var readDb = new FrayloDbContext(dbOptions))
            {
                if (!await HasBrowserBudgetAsync(readDb, profile.ProfileId, ct)) throw new InvalidOperationException("Đang hết chỗ mở trình duyệt; hãy chờ tác vụ khác hoàn tất rồi tải lại nhóm.");
                if (await readDb.FacebookPostTasks.AnyAsync(t => t.ProfileId == profile.ProfileId && (t.State == "Preparing" || t.State == "Submitting"), ct))
                    throw new InvalidOperationException("Tài khoản đang đăng bài; hãy chờ hoàn tất trước khi tải nhóm.");
            }
            FacebookGroupDiscovery discovery;
            try { discovery = await browser.DiscoverGroupsAsync(profile.ProfileId, profile.AccountIdentity!, ct); }
            catch (FacebookNeedsUserException ex)
            {
                await InvalidateSessionAsync(profile.ProfileId, ex.ReasonCode);
                throw;
            }
            var items = discovery.Groups.Select(g => new { g.Name, Url = NormalizeGroupUrl(g.Url) }).GroupBy(g => g.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            if (items.Any(g => string.IsNullOrWhiteSpace(g.Name))) throw new InvalidOperationException("Danh sách nhóm thiếu tên; chưa lưu thay đổi.");
            await _changes.WaitAsync(ct);
            try
            {
                await using var db = new FrayloDbContext(dbOptions);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var existing = await db.FacebookTargets.Where(t => t.ProfileId == profile.ProfileId && !t.Id.StartsWith("personal:")).ToListAsync(ct);
                var now = Now();
                foreach (var item in items)
                {
                    var target = existing.SingleOrDefault(t => string.Equals(t.Url, item.Url, StringComparison.OrdinalIgnoreCase));
                    if (target == null)
                    {
                        target = new() { Id = Guid.NewGuid().ToString("N"), ProfileId = profile.ProfileId, Url = item.Url, CreatedAtUtc = now };
                        db.FacebookTargets.Add(target);
                        existing.Add(target);
                    }
                    target.Name = item.Name.Trim(); target.Lifecycle = "Active"; target.UpdatedAtUtc = now;
                }
                // Kết quả chưa đủ không được làm mất những nhóm chưa tải tới.
                if (discovery.IsComplete)
                {
                    var urls = items.Select(i => i.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var target in existing.Where(t => !urls.Contains(t.Url))) { target.Lifecycle = "Archived"; target.UpdatedAtUtc = now; }
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new(existing.Where(t => t.Lifecycle == "Active").Select(GroupDto).ToArray(), discovery.IsComplete, discovery.Message);
            }
            finally { _changes.Release(); }
        }
        finally { _dispatch.Release(); }
    }

    public async Task<IReadOnlyList<FacebookTaskDto>> CreateBatchAsync(FacebookCreateBatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParse(request.BatchRequestId, out var batchGuid)) throw new ArgumentException("Mã lô không hợp lệ.");
        if (request.TargetIds == null || request.TargetIds.Count == 0 || request.TargetIds.Any(string.IsNullOrWhiteSpace) || request.TargetIds.Distinct(StringComparer.Ordinal).Count() != request.TargetIds.Count)
            throw new ArgumentException("Chọn ít nhất một nhóm, không lặp nhóm.");
        if (request.ImageAssetIds == null || request.ImageAssetIds.Count > 10 || request.ImageAssetIds.Distinct().Count() != request.ImageAssetIds.Count)
            throw new ArgumentException("Chọn tối đa 10 ảnh khác nhau.");
        if (request.Text == null || request.Text.EnumerateRunes().Count() > 10000 || (string.IsNullOrWhiteSpace(request.Text) && request.ImageAssetIds.Count == 0))
            throw new ArgumentException("Bài cần có chữ hoặc ảnh, tối đa 10.000 ký tự.");
        if (request.TargetKind is not ("Group" or "Profile")) throw new ArgumentException("Loại nơi đăng không hợp lệ.");
        var personal = request.TargetKind == "Profile";
        if (personal && (request.TargetIds.Count != 1 || request.TargetIds[0] != "personal:" + request.AccountId))
            throw new ArgumentException("Chỉ được đăng lên trang cá nhân của tài khoản đã chọn.");
        if (!personal && request.TargetIds.Any(id => id.StartsWith("personal:", StringComparison.Ordinal)))
            throw new ArgumentException("Đích cá nhân không phải nhóm.");
        var batchId = batchGuid.ToString("N");
        var due = ParseDueAt(request.DueAtUtc);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length > 80) throw new ArgumentException("Tên bài tối đa 80 ký tự.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            request.AccountId,
            request.Text,
            request.RewriteContent,
            TargetIds = request.TargetIds,
            Name = name,
            request.ImageAssetIds,
            DueAtUtc = due?.ToString("O")
        }))));
        await _changes.WaitAsync(ct);
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var prior = await db.FacebookPostBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == batchId, ct);
            if (prior != null)
            {
                if (prior.RequestHash != hash) throw new ArgumentException("Mã lô đã được dùng với nội dung hoặc danh sách nhóm khác.");
                return await TaskDtosAsync(db, await db.FacebookPostTasks.AsNoTracking().Where(t => t.BatchId == batchId).OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct), ct);
            }
            var profile = await ConnectedProfileAsync(request.AccountId, ct);
            if (personal)
            {
                var targetId = "personal:" + request.AccountId;
                var target = await db.FacebookTargets.SingleOrDefaultAsync(t => t.Id == targetId, ct);
                var stamp = Now();
                if (target == null)
                {
                    target = new() { Id = targetId, ProfileId = request.AccountId, CreatedAtUtc = stamp };
                    db.FacebookTargets.Add(target);
                }
                var match = System.Text.RegularExpressions.Regex.Match(profile.AccountIdentity!, @"^Facebook [(]([0-9]+)[)]$");
                if (!match.Success) throw new ArgumentException("Danh tính Facebook không hợp lệ.");
                target.Name = "Trang cá nhân · " + (profile.DisplayName ?? profile.AccountIdentity);
                target.Url = "https://www.facebook.com/profile.php?id=" + match.Groups[1].Value;
                target.Lifecycle = "Active"; target.UpdatedAtUtc = stamp;
                await db.SaveChangesAsync(ct);
            }
            var targets = await db.FacebookTargets.AsNoTracking().Where(t => request.TargetIds.Contains(t.Id) && t.ProfileId == request.AccountId && t.Lifecycle == "Active").ToListAsync(ct);
            if (targets.Count != request.TargetIds.Count) throw new ArgumentException("Có nhóm không thuộc tài khoản đã chọn hoặc không còn trong danh sách; hãy tải lại nhóm.");
            var assets = await db.Assets.AsNoTracking().Where(a => request.ImageAssetIds.Contains(a.Id) && a.AssetType == "Image" && a.Status == "Linked").ToListAsync(ct);
            if (assets.Count != request.ImageAssetIds.Count || assets.Any(a => a.Sha256Hash.Length != 64)) throw new ArgumentException("Ảnh đã chọn không còn khả dụng hoặc thiếu mã kiểm tra.");
            var now = Now();
            var batch = new FacebookPostBatch { Id = batchId, ProfileId = profile.ProfileId, RequestHash = hash, Name = name, CreatedAtUtc = now };
            db.FacebookPostBatches.Add(batch);
            var tasks = new List<FacebookPostTask>();
            foreach (var targetId in request.TargetIds)
            {
                var target = targets.Single(t => t.Id == targetId);
                var task = new FacebookPostTask
                {
                    Id = Guid.NewGuid().ToString("N"),
                    BatchId = batchId,
                    SortOrder = tasks.Count,
                    TargetId = target.Id,
                    ProfileId = profile.ProfileId,
                    AccountIdentitySnapshot = profile.AccountIdentity!,
                    GroupNameSnapshot = target.Name,
                    GroupUrlSnapshot = target.Url,
                    TextSnapshot = request.Text,
                    State = "Queued",
                    Message = due == null ? "Đang chờ đăng; mỗi tài khoản cách ít nhất 5 phút." : ScheduledMessage(due.Value),
                    ActivityLogJson = FacebookActivityLog.Append("[]", now, due == null ? "queued" : "scheduled",
                        due == null ? "Đã đưa lượt đăng vào hàng đợi." : ScheduledMessage(due.Value)),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                tasks.Add(task); db.FacebookPostTasks.Add(task);
                if (due != null) db.FacebookTaskSchedules.Add(new() { TaskId = task.Id, DueAtUtc = due.Value.ToString("O") });
                for (var i = 0; i < request.ImageAssetIds.Count; i++)
                {
                    var asset = assets.Single(a => a.Id == request.ImageAssetIds[i]);
                    db.FacebookPostTaskMedia.Add(new() { TaskId = task.Id, AssetId = asset.Id, Sha256 = asset.Sha256Hash, SortOrder = i });
                }
            }
            if (!personal && request.RewriteContent && !string.IsNullOrWhiteSpace(request.Text))
            {
                if (rewrite == null) throw new InvalidOperationException("AI viết lại chưa sẵn sàng. Chọn Đăng nguyên bản hoặc thử lại sau.");
                await rewrite.StageAsync(db, batch, tasks, ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (batch.RequiresRewrite) rewrite!.Wake();
            return await TaskDtosAsync(db, tasks, ct);
        }
        finally { _changes.Release(); }
    }

    public async Task<FacebookPostDto> PostActionAsync(FacebookPostActionRequest request, CancellationToken ct = default)
    {
        if (request.Action is not ("pause" or "resume" or "cancel" or "reschedule" or "now"))
            throw new ArgumentException("Thao tác bài đăng không hợp lệ.");
        DateTimeOffset? due = request.Action == "reschedule" ? ParseDueAt(request.DueAtUtc) ?? throw new ArgumentException("Cần giờ hẹn mới.") : null;
        await _changes.WaitAsync(ct);
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var batch = await db.FacebookPostBatches.SingleOrDefaultAsync(b => b.Id == request.BatchId, ct)
                ?? throw new KeyNotFoundException("Không tìm thấy bài đăng.");
            if (batch.Revision != request.Revision) throw new InvalidOperationException("Bài đã thay đổi; tải lại trước khi thao tác.");
            var tasks = await db.FacebookPostTasks.Where(t => t.BatchId == batch.Id).OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
            if (request.Action == "reschedule" && tasks.Any(t => t.SubmittedAtUtc != null || t.State is "Preparing" or "Submitting"))
                throw new InvalidOperationException("Chỉ đổi giờ cả bài khi chưa bắt đầu gửi.");
            if (request.Action is "resume" or "now")
            {
                var profile = await ConnectedProfileAsync(batch.ProfileId, ct);
                if (tasks.Any(t => t.AccountIdentitySnapshot != profile.AccountIdentity))
                    throw new InvalidOperationException("Danh tính tài khoản đã đổi; hãy tạo bài mới.");
            }
            batch.Revision++;
            batch.Paused = request.Action == "pause" || (batch.Paused && request.Action == "reschedule");
            foreach (var task in tasks.Where(t => t.SubmittedAtUtc == null && t.State is "Queued" or "NeedsUser" or "Preparing"))
            {
                task.Revision++; task.UpdatedAtUtc = Now();
                if (request.Action == "cancel")
                {
                    task.State = "Canceled"; task.ReasonCode = null; task.CancelRequested = true; task.Message = "Đã hủy phần chưa gửi của bài.";
                }
                else if (request.Action == "pause")
                {
                    if (task.State == "Preparing") task.State = "Queued";
                    task.Message = "Bài đang tạm dừng; lượt đã gửi vẫn hoàn tất.";
                }
                else
                {
                    task.State = "Queued"; task.ReasonCode = null; task.Message = "Đang chờ lượt đăng (cách ít nhất 5 phút).";
                }
                task.ActivityLogJson = FacebookActivityLog.Append(task.ActivityLogJson, task.UpdatedAtUtc, request.Action,
                    due.HasValue ? ScheduledMessage(due.Value) : task.Message);
                task.LeaseOwnerId = null; task.LeaseExpiresAtUtc = null;
                if (request.Action is "reschedule" or "now" or "resume")
                {
                    var schedule = await db.FacebookTaskSchedules.SingleOrDefaultAsync(s => s.TaskId == task.Id, ct);
                    if (request.Action == "reschedule")
                    {
                        if (schedule == null) db.FacebookTaskSchedules.Add(new() { TaskId = task.Id, DueAtUtc = due!.Value.ToString("O") });
                        else schedule.DueAtUtc = due!.Value.ToString("O");
                    }
                    else if (schedule != null && (request.Action == "now" || DateTimeOffset.Parse(schedule.DueAtUtc) <= UtcNow)) db.FacebookTaskSchedules.Remove(schedule);
                }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (request.Action == "cancel" && rewrite != null) await rewrite.CancelBatchAsync(batch.Id, ct);
            if (request.Action is "resume" or "now") rewrite?.Wake();
            return new(batch.Id, batch.Name, batch.ProfileId, batch.Paused, batch.Revision, batch.CreatedAtUtc, await TaskDtosAsync(db, tasks, ct),
                batch.OriginalTextSnapshot, batch.RequiresRewrite ? await FacebookContentRewriteService.ViewsAsync(db, batch.Id, ct) : null);
        }
        finally { _changes.Release(); }
    }

    public async Task<FacebookTaskDto> CancelAsync(FacebookTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = new FrayloDbContext(dbOptions);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await db.FacebookPostTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TaskId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy bài đăng.");
        var now = Now();
        const string message = "Đã hủy trước khi gửi lệnh đăng.";
        var log = FacebookActivityLog.Append(task.ActivityLogJson, now, "cancel", message);
        await db.FacebookPostTasks.Where(t => t.Id == task.Id && (t.State == "Queued" || t.State == "NeedsUser" || t.State == "Preparing"))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Canceled").SetProperty(t => t.ReasonCode, (string?)null).SetProperty(t => t.CancelRequested, true)
                .SetProperty(t => t.Message, message).SetProperty(t => t.ActivityLogJson, log)
                .SetProperty(t => t.UpdatedAtUtc, now).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
        await tx.CommitAsync(ct);
        return await ReadTaskAsync(db, request.TaskId, ct);
    }

    public async Task<FacebookTaskDto> ResumeAsync(FacebookTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = new FrayloDbContext(dbOptions);
        var task = await db.FacebookPostTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TaskId, ct) ?? throw new KeyNotFoundException("Không tìm thấy bài đăng.");
        if (task.State != "NeedsUser" || task.SubmittedAtUtc != null) throw new InvalidOperationException("Chỉ tiếp tục được bài chưa gửi đang chờ bạn xác nhận.");
        var profile = await ConnectedProfileAsync(task.ProfileId, ct);
        if (profile.AccountIdentity != task.AccountIdentitySnapshot) throw new InvalidOperationException("Danh tính tài khoản đã thay đổi; hãy tạo lô mới.");
        var now = Now();
        const string message = "Đã xác nhận tiếp tục; đang chờ lượt đăng.";
        var log = FacebookActivityLog.Append(task.ActivityLogJson, now, "resume", message);
        await db.FacebookPostTasks.Where(t => t.Id == task.Id && t.State == "NeedsUser" && t.SubmittedAtUtc == null && t.Revision == task.Revision)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Queued").SetProperty(t => t.ReasonCode, (string?)null)
                .SetProperty(t => t.Message, message).SetProperty(t => t.ActivityLogJson, log).SetProperty(t => t.UpdatedAtUtc, now).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
        return await ReadTaskAsync(db, task.Id, ct);
    }

    /// <summary>Đăng ngay một bài đã hẹn giờ: bỏ giờ hẹn, tác vụ vào lượt đăng theo giãn cách thường.</summary>
    public async Task<FacebookTaskDto> PostNowAsync(FacebookTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = new FrayloDbContext(dbOptions);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await db.FacebookPostTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TaskId && t.State == "Queued", ct);
        if (task == null || await db.FacebookTaskSchedules.Where(s => s.TaskId == request.TaskId).ExecuteDeleteAsync(ct) != 1)
            throw new InvalidOperationException("Chỉ đăng ngay được bài đang hẹn giờ và chưa bắt đầu gửi.");
        var now = Now();
        const string message = "Đăng ngay; đang chờ lượt đăng.";
        var log = FacebookActivityLog.Append(task.ActivityLogJson, now, "now", message);
        await db.FacebookPostTasks.Where(t => t.Id == task.Id && t.State == "Queued")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Message, message).SetProperty(t => t.ReasonCode, (string?)null)
                .SetProperty(t => t.ActivityLogJson, log).SetProperty(t => t.UpdatedAtUtc, now).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
        await tx.CommitAsync(ct);
        return await ReadTaskAsync(db, request.TaskId, ct);
    }

    /// <summary>Đổi giờ hẹn của bài chưa gửi (đang hẹn hoặc đã lỡ lịch / chờ người dùng); nội dung vẫn là bản chụp.</summary>
    public async Task<FacebookTaskDto> RescheduleAsync(FacebookRescheduleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var due = ParseDueAt(request.DueAtUtc) ?? throw new ArgumentException("Cần chọn giờ hẹn.");
        await using var db = new FrayloDbContext(dbOptions);
        var task = await db.FacebookPostTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TaskId, ct) ?? throw new KeyNotFoundException("Không tìm thấy bài đăng.");
        if (task.SubmittedAtUtc != null || task.State is not ("Queued" or "NeedsUser")) throw new InvalidOperationException("Chỉ đổi giờ được bài chưa gửi đang chờ.");
        if (task.State == "NeedsUser")
        {
            var profile = await ConnectedProfileAsync(task.ProfileId, ct);
            if (profile.AccountIdentity != task.AccountIdentitySnapshot) throw new InvalidOperationException("Danh tính tài khoản đã thay đổi; hãy tạo lô mới.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = Now();
        var message = ScheduledMessage(due);
        var log = FacebookActivityLog.Append(task.ActivityLogJson, now, "reschedule", message);
        var changed = await db.FacebookPostTasks.Where(t => t.Id == task.Id && t.SubmittedAtUtc == null && t.Revision == task.Revision && (t.State == "Queued" || t.State == "NeedsUser"))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Queued").SetProperty(t => t.ReasonCode, (string?)null)
                .SetProperty(t => t.Message, message).SetProperty(t => t.ActivityLogJson, log).SetProperty(t => t.UpdatedAtUtc, now).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
        if (changed != 1) throw new InvalidOperationException("Bài đã bắt đầu xử lý nên không đổi giờ được.");
        await db.FacebookTaskSchedules.Where(s => s.TaskId == task.Id).ExecuteDeleteAsync(ct);
        db.FacebookTaskSchedules.Add(new() { TaskId = task.Id, DueAtUtc = due.ToString("O") });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ReadTaskAsync(db, task.Id, ct);
    }

    public async Task RunQueueAsync(CancellationToken ct)
    {
        var recovered = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!recovered) { await RecoverInterruptedAsync(ct); recovered = true; }
                if (!await ProcessNextAsync(ct)) await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                Console.Error.WriteLine("Facebook queue: SQLite đang bận, giữ nguyên tác vụ và chờ quét lại.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                Console.Error.WriteLine("Facebook queue: SQLite đang bận khi ghi, chờ quét lại.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        await using var db = new FrayloDbContext(dbOptions);
        var tasks = await db.FacebookPostTasks.AsNoTracking().Where(t => t.State == "Preparing" || t.State == "Submitting" || t.State == "Queued").ToListAsync(ct);
        foreach (var task in tasks)
        {
            var previousState = task.State;
            if (task.State == "Queued")
            {
                if (string.CompareOrdinal(task.CreatedAtUtc, _startedAt) >= 0) continue;
                var scheduled = await db.FacebookTaskSchedules.AsNoTracking().Where(s => s.TaskId == task.Id).Select(s => s.DueAtUtc).SingleOrDefaultAsync(ct);
                // Bài hẹn giờ còn trong tương lai vẫn chờ giờ hẹn; đã quá giờ lúc ứng dụng tắt thì không đăng muộn.
                if (scheduled != null && DateTimeOffset.Parse(scheduled) > UtcNow) continue;
                task.State = "NeedsUser";
                task.ReasonCode = scheduled != null ? "schedule_missed" : "app_restarted";
                task.Message = scheduled != null
                    ? "Đã lỡ giờ hẹn vì ứng dụng không chạy. Chọn Đăng ngay, Đổi giờ hoặc Hủy."
                    : "Ứng dụng đã khởi động lại; kiểm tra nội dung rồi chọn Tiếp tục nếu vẫn muốn đăng.";
            }
            else
            {
                if (OwnerAlive(task.LeaseOwnerId)) continue;
                task.State = task.SubmittedAtUtc != null || task.State == "Submitting" ? "Unknown" : "Failed";
                task.ReasonCode = "app_interrupted";
                task.Message = task.State == "Unknown" ? "Ứng dụng dừng sau khi bắt đầu gửi; hãy kiểm tra nhóm. Không tự đăng lại." : "Ứng dụng dừng trước khi gửi; chưa đăng và không tự thử lại.";
            }
            var stamp = Now();
            var log = FacebookActivityLog.Append(task.ActivityLogJson, stamp, "recovered", task.Message, task.ReasonCode);
            await db.FacebookPostTasks.Where(t => t.Id == task.Id && t.Revision == task.Revision && t.State == previousState)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, task.State).SetProperty(t => t.ReasonCode, task.ReasonCode)
                    .SetProperty(t => t.Message, task.Message).SetProperty(t => t.ActivityLogJson, log).SetProperty(t => t.LeaseOwnerId, (string?)null).SetProperty(t => t.LeaseExpiresAtUtc, (string?)null)
                    .SetProperty(t => t.UpdatedAtUtc, stamp).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        if (!await _dispatch.WaitAsync(0, ct)) return false;
        try
        {
            if (rewrite != null) await rewrite.ApplyPendingAsync(ct);
            foreach (var outcome in _pendingOutcomes.Values.ToArray()) await PersistOutcomeAsync(outcome);
            await using var db = new FrayloDbContext(dbOptions);
            var candidates = await db.FacebookPostTasks.AsNoTracking().Where(t => t.State == "Queued" &&
                db.FacebookPostBatches.Any(b => b.Id == t.BatchId && (!b.RequiresRewrite ||
                    db.FacebookRewriteChunks.Any(c => c.BatchId == b.Id) && !db.FacebookRewriteChunks.Any(c => c.BatchId == b.Id && c.AppliedAtUtc == null)))).OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
            var allProfiles = await profiles.ListProfilesAsync(ct);
            var enabledProfiles = await db.ProviderAccounts.AsNoTracking().Where(a => a.ProviderKey == "facebook" && a.Lifecycle == "Active" && a.Enabled).Select(a => a.ProfileId).ToListAsync(ct);
            var schedules = await db.FacebookTaskSchedules.AsNoTracking().ToDictionaryAsync(s => s.TaskId, s => DateTimeOffset.Parse(s.DueAtUtc), ct);
            var batches = await db.FacebookPostBatches.AsNoTracking().ToDictionaryAsync(b => b.Id, ct);
            candidates = candidates.Where(t => !batches[t.BatchId].Paused)
                .OrderBy(t => schedules.GetValueOrDefault(t.Id, DateTimeOffset.Parse(batches[t.BatchId].CreatedAtUtc)))
                .ThenBy(t => batches[t.BatchId].CreatedAtUtc).ThenBy(t => t.BatchId).ThenBy(t => t.SortOrder).ThenBy(t => t.Id).ToList();
            foreach (var task in candidates)
            {
                ct.ThrowIfCancellationRequested();
                // Chưa tới giờ hẹn thì chưa kiểm phiên hay mở browser.
                if (schedules.TryGetValue(task.Id, out var dueAt) && dueAt > UtcNow) continue;
                var profile = allProfiles.SingleOrDefault(p => p.ProfileId == task.ProfileId);
                if (profile == null || profile.Kind != "Facebook" || profile.Lifecycle != "Active" || profile.SessionStatus != "Connected" || profile.AccountIdentity != task.AccountIdentitySnapshot || !enabledProfiles.Contains(task.ProfileId))
                {
                    await UpdateQueuedAsync(db, task, "NeedsUser", "session_expired", "Hãy bật và xác minh đúng tài khoản Facebook trong Cài đặt, rồi chọn Tiếp tục.", ct);
                    continue;
                }
                if (controller.IsLoginWindowOpen(task.ProfileId) || profile.OperationalState == "Quiescing" || profile.VisibilityState != "Hidden")
                {
                    await UpdateQueuedAsync(db, task, "Queued", "login_in_progress", "Chờ đóng cửa sổ đăng nhập Facebook.", ct);
                    continue;
                }
                if (await db.FacebookAnalyticsCaptures.AnyAsync(c => c.Status == "Running" && db.FacebookAnalyticsPages.Any(p => p.Id == c.PageId && p.ProfileId == task.ProfileId), ct))
                {
                    await UpdateQueuedAsync(db, task, "Queued", "analytics_running", "Chờ lượt cập nhật Analytics của tài khoản này.", ct);
                    continue;
                }
                if (await db.FacebookPostTasks.AnyAsync(t => t.ProfileId == task.ProfileId && (t.State == "Preparing" || t.State == "Submitting"), ct))
                {
                    await UpdateQueuedAsync(db, task, "Queued", "profile_busy", "Chờ lượt đăng trước của tài khoản này.", ct);
                    continue;
                }
                if (!await HasBrowserBudgetAsync(db, task.ProfileId, ct))
                {
                    await UpdateQueuedAsync(db, task, "Queued", "browser_budget", "Chờ chỗ trống để mở trình duyệt Facebook.", ct);
                    continue;
                }
                var eligible = await NextEligibleAtAsync(db, task.ProfileId, ct);
                if (eligible > UtcNow)
                {
                    await UpdateQueuedAsync(db, task, "Queued", "pacing", $"Chờ giãn cách; sớm nhất {eligible.ToLocalTime():HH:mm dd/MM/yyyy}.", ct);
                    continue;
                }
                var now = Now();
                const string preparing = "Đang kiểm tra tệp đính kèm và nội dung đã lưu.";
                var log = FacebookActivityLog.Append(task.ActivityLogJson, now, "checking_input", preparing);
                int claimed;
                try
                {
                    claimed = await db.FacebookPostTasks.Where(t => t.Id == task.Id && t.Revision == task.Revision && t.State == "Queued" && !t.CancelRequested && db.FacebookPostBatches.Any(b => b.Id == t.BatchId && !b.Paused && (!b.RequiresRewrite ||
                            db.FacebookRewriteChunks.Any(c => c.BatchId == b.Id) && !db.FacebookRewriteChunks.Any(c => c.BatchId == b.Id && c.AppliedAtUtc == null)))
                        && !db.FacebookAnalyticsCaptures.Any(c => c.Status == "Running" && db.FacebookAnalyticsPages.Any(p => p.Id == c.PageId && p.ProfileId == t.ProfileId)))
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Preparing").SetProperty(t => t.LeaseOwnerId, _owner)
                            .SetProperty(t => t.LeaseExpiresAtUtc, UtcNow.AddMinutes(15).ToString("O"))
                            .SetProperty(t => t.Message, preparing).SetProperty(t => t.ReasonCode, (string?)null)
                            .SetProperty(t => t.ActivityLogJson, log).SetProperty(t => t.UpdatedAtUtc, now).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { continue; }
                if (claimed != 1) continue;
                await ExecuteTaskAsync(task, ct);
                return true;
            }
            return false;
        }
        finally { _dispatch.Release(); }
    }

    private async Task ExecuteTaskAsync(FacebookPostTask task, CancellationToken ct)
    {
        var files = new List<FileStream>();
        var paths = new List<string>();
        var submitted = false;
        var state = "Failed"; var reason = "preparation_failed"; var message = "Không thể chuẩn bị bài đăng.";
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            var media = await db.FacebookPostTaskMedia.AsNoTracking().Where(m => m.TaskId == task.Id).OrderBy(m => m.SortOrder).ToListAsync(ct);
            foreach (var item in media)
            {
                var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == item.AssetId && a.AssetType == "Image" && a.Status == "Linked", ct)
                    ?? throw new FacebookPostException("media_changed", "Ảnh không còn khả dụng; chưa mở Facebook.");
                var root = await roots.ResolveRootPathAsync(asset.StorageRootId, ct);
                var path = StoragePathGuard.CombineAndVerify(root, asset.RelativePath);
                if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"))
                    throw new FacebookPostException("media_changed", "Định dạng ảnh không được hỗ trợ; chưa mở Facebook.");
                FileStream stream;
                try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (IOException) { throw new FacebookPostException("media_changed", "Tệp ảnh không còn đọc được; chưa mở Facebook."); }
                files.Add(stream);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new FacebookPostException("media_changed", "Tệp ảnh đã thay đổi sau khi tạo lô; chưa mở Facebook.");
                paths.Add(path);
            }
            Func<Task> beforeSubmit = async () =>
            {
                ct.ThrowIfCancellationRequested();
                var profile = await ConnectedProfileAsync(task.ProfileId, ct);
                if (profile.AccountIdentity != task.AccountIdentitySnapshot) throw new FacebookPostException("identity_mismatch", "Tài khoản đã đổi danh tính; chưa đăng.");
                await using var checkpoint = new FrayloDbContext(dbOptions);
                var stamp = Now();
                const string submitting = "Đã bắt đầu gửi lệnh đăng; đang chờ Facebook xác nhận.";
                var current = await checkpoint.FacebookPostTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id, ct);
                var log = FacebookActivityLog.Append(current.ActivityLogJson, stamp, "submit_checkpoint", submitting);
                var changed = await checkpoint.FacebookPostTasks.Where(t => t.Id == task.Id && t.State == "Preparing" && !t.CancelRequested && t.LeaseOwnerId == _owner && checkpoint.FacebookPostBatches.Any(b => b.Id == t.BatchId && !b.Paused && (!b.RequiresRewrite ||
                    checkpoint.FacebookRewriteChunks.Any(c => c.BatchId == b.Id) && !checkpoint.FacebookRewriteChunks.Any(c => c.BatchId == b.Id && c.AppliedAtUtc == null))))
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Submitting").SetProperty(t => t.SubmittedAtUtc, stamp)
                        .SetProperty(t => t.Message, submitting).SetProperty(t => t.ActivityLogJson, log).SetProperty(t => t.UpdatedAtUtc, stamp).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
                if (changed != 1) throw new OperationCanceledException("Bài đã hủy hoặc không còn quyền thực thi; chưa bấm Đăng.");
                submitted = true;
            };
            Task Report(string code, string detail) => ReportStageAsync(task.Id, code, detail);
            await Report("opening_browser", "Đang mở trình duyệt Facebook chạy ẩn.");
            var result = task.TargetId.StartsWith("personal:", StringComparison.Ordinal)
                ? await (personalPoster ?? new FacebookPoster(controller)).PostAsync(new(task.ProfileId, task.AccountIdentitySnapshot, task.TextSnapshot, paths, ReportStage: Report), beforeSubmit, ct)
                : await browser.PostGroupAsync(new(task.ProfileId, task.AccountIdentitySnapshot, task.GroupUrlSnapshot, task.GroupNameSnapshot, task.TextSnapshot, paths, Report), beforeSubmit, ct);
            if (!submitted) throw new InvalidOperationException("Adapter không ghi checkpoint trước khi đăng.");
            state = result.Outcome == FacebookPostOutcome.PendingApproval ? "PendingApproval" : "Published";
            reason = "confirmed";
            message = state == "Published" ? "Facebook đã xác nhận xuất bản bài đăng." : "Chờ Facebook duyệt; bài chưa được xuất bản.";
        }
        catch (FacebookNeedsUserException ex) when (!submitted)
        {
            state = "NeedsUser"; reason = ex.ReasonCode; message = ex.Message;
            await InvalidateSessionAsync(task.ProfileId, ex.ReasonCode);
        }
        catch (FacebookPostException ex)
        {
            state = submitted && ex.ReasonCode != "rejected_by_facebook" ? "Unknown" : "Failed"; reason = ex.ReasonCode; message = ex.Message;
        }
        catch (Exception ex)
        {
            state = submitted ? "Unknown" : ex is OperationCanceledException ? "Canceled" : "Failed";
            reason = submitted ? "outcome_unknown" : ex is OperationCanceledException ? "canceled" : "preparation_failed";
            message = submitted ? "Không xác định được kết quả sau khi bắt đầu gửi. Kiểm tra nơi đăng trước khi tạo lô mới; không tự đăng lại." : ex is OperationCanceledException ? "Đã dừng trước khi gửi lệnh đăng." : "Không thể chuẩn bị bài đăng. Kiểm tra tài khoản, ảnh và kết nối rồi tạo yêu cầu mới nếu cần.";
        }
        finally { foreach (var file in files) await file.DisposeAsync(); }
        var outcome = new TaskOutcome(task.Id, state, reason, message);
        _pendingOutcomes[task.Id] = outcome;
        await PersistOutcomeAsync(outcome);
    }

    private async Task ReportStageAsync(string taskId, string code, string message)
    {
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            var owned = db.FacebookPostTasks.Where(t => t.Id == taskId && t.LeaseOwnerId == _owner && t.State == "Preparing");
            var task = await owned.AsNoTracking().SingleOrDefaultAsync();
            if (task == null) return;
            var stamp = Now();
            var log = FacebookActivityLog.Append(task.ActivityLogJson, stamp, code, message);
            await owned.Where(t => t.Revision == task.Revision)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ActivityLogJson, log).SetProperty(t => t.Message, message)
                    .SetProperty(t => t.UpdatedAtUtc, stamp).SetProperty(t => t.Revision, t => t.Revision + 1));
        }
        catch (SqliteException ex)
        {
            // Một mốc chẩn đoán trung gian không được đổi kết quả thao tác trên Facebook.
            Console.Error.WriteLine($"Facebook task {taskId}: {code} log unavailable (SQLite {ex.SqliteErrorCode}).");
        }
    }

    private async Task PersistOutcomeAsync(TaskOutcome outcome)
    {
        // Giữ kết quả trong RAM qua lần quét sau nếu SQLite bận; không chạy adapter lần nữa.
        await using var db = new FrayloDbContext(dbOptions);
        var stored = await db.FacebookPostTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == outcome.TaskId);
        if (stored == null || stored.LeaseOwnerId != _owner || stored.State is not ("Preparing" or "Submitting"))
        {
            _pendingOutcomes.Remove(outcome.TaskId);
            return;
        }
        var uncertain = stored.SubmittedAtUtc != null && outcome.Reason != "rejected_by_facebook" && outcome.State is "Failed" or "Canceled" or "NeedsUser";
        var finalState = uncertain ? "Unknown" : outcome.State;
        var finalMessage = uncertain ? "Không xác định được kết quả sau khi bắt đầu gửi. Kiểm tra nơi đăng trước khi tạo lô mới; không tự đăng lại." : outcome.Message;
        var now = Now();
        var log = FacebookActivityLog.Append(stored.ActivityLogJson, now, finalState, outcome.Reason == "rejected_by_facebook"
            ? "Facebook đã từ chối bài đăng." : finalMessage, outcome.Reason);
        await db.FacebookPostTasks.Where(t => t.Id == outcome.TaskId && t.LeaseOwnerId == _owner && (t.State == "Preparing" || t.State == "Submitting"))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, finalState).SetProperty(t => t.ReasonCode, outcome.Reason).SetProperty(t => t.Message, finalMessage)
                .SetProperty(t => t.ActivityLogJson, log).SetProperty(t => t.LeaseOwnerId, (string?)null).SetProperty(t => t.LeaseExpiresAtUtc, (string?)null)
                .SetProperty(t => t.UpdatedAtUtc, now).SetProperty(t => t.Revision, t => t.Revision + 1), CancellationToken.None);
        _pendingOutcomes.Remove(outcome.TaskId);
    }

    private async Task InvalidateSessionAsync(string profileId, string reason)
    {
        try { await profiles.UpdateProfileStateAsync(profileId, sessionStatus: "Unverified", reasonCode: reason, cancellationToken: CancellationToken.None); }
        catch (Exception)
        {
            // Lỗi lưu trạng thái không thay thế thông báo phiên hết hạn gốc hoặc làm mất kết quả tác vụ.
            Console.Error.WriteLine("Facebook: chưa cập nhật được trạng thái phiên; cần xác minh lại trong Cài đặt.");
        }
    }

    private async Task<bool> HasBrowserBudgetAsync(FrayloDbContext db, string profileId, CancellationToken ct)
    {
        if (controller.IsRunning(profileId)) return true;
        var all = await profiles.ListProfilesAsync(ct);
        var limitString = await db.AppSettings.Where(s => s.Key == "scheduler.max_open_browsers").Select(s => s.Value).SingleOrDefaultAsync(ct);
        var limit = int.TryParse(limitString, out var configured) && configured > 0 ? configured : 3;
        var occupied = await db.ActiveSlotOccupancies.AsNoTracking()
            .Join(db.ProviderAccounts.AsNoTracking(), o => o.ProviderAccountId, a => a.Id, (o, a) => a.ProfileId).Distinct().ToListAsync(ct);
        var uncertain = await db.Jobs.AsNoTracking().Where(j => j.State == "AwaitingUser")
            .Join(db.ExecutionBindings.AsNoTracking(), j => j.Id, b => b.JobId, (j, b) => b.ProfileId).Distinct().ToListAsync(ct);
        var open = occupied.Concat(uncertain).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in all.Where(p => controller.IsRunning(p.ProfileId) || controller.IsLoginWindowOpen(p.ProfileId) || p.OperationalState is "Running" or "Idle" or "Quiescing" || p.ActiveJobCount > 0))
            open.Add(profile.ProfileId);
        return open.Contains(profileId) || open.Count < limit;
    }

    private async Task<BrowserProfileInfo> ConnectedProfileAsync(string profileId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profileId)) throw new ArgumentException("Cần chọn tài khoản Facebook.");
        var profile = await profiles.GetProfileAsync(profileId, ct);
        if (profile == null || profile.Kind != "Facebook" || profile.Lifecycle != "Active" || profile.SessionStatus != "Connected" || string.IsNullOrWhiteSpace(profile.AccountIdentity))
            throw new InvalidOperationException("Hãy đăng nhập và xác minh tài khoản Facebook trong Cài đặt trước.");
        await using var db = new FrayloDbContext(dbOptions);
        if (!await db.ProviderAccounts.AsNoTracking().AnyAsync(a => a.ProfileId == profileId && a.ProviderKey == "facebook" && a.Lifecycle == "Active" && a.Enabled, ct))
            throw new InvalidOperationException("Tài khoản Facebook đã tắt hoặc không còn được liên kết. Hãy bật tài khoản trong Cài đặt.");
        return profile;
    }

    public static readonly TimeSpan PacingBase = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PacingMaxJitter = TimeSpan.FromSeconds(30);

    /// <summary>Giãn cách sau một lượt gửi: 5 phút cộng 0–30 giây để không đều tăm tắp. Độ lệch suy ra từ mã lượt vừa gửi nên ổn định giữa các lần kiểm tra và sau khi khởi động lại.</summary>
    public static TimeSpan PacingGap(string lastTaskId) =>
        PacingBase + TimeSpan.FromMilliseconds(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(lastTaskId))) % ((long)PacingMaxJitter.TotalMilliseconds + 1));

    public static async Task<DateTimeOffset> NextEligibleAtAsync(FrayloDbContext db, string profileId, CancellationToken ct = default)
    {
        var last = await db.FacebookPostTasks.AsNoTracking().Where(t => t.ProfileId == profileId && t.SubmittedAtUtc != null)
            .OrderByDescending(t => t.SubmittedAtUtc).Select(t => new { t.Id, t.SubmittedAtUtc }).FirstOrDefaultAsync(ct);
        return last == null ? DateTimeOffset.MinValue : DateTimeOffset.Parse(last.SubmittedAtUtc!) + PacingGap(last.Id);
    }

    public static string NormalizeGroupUrl(string url) => FacebookGroupBrowser.NormalizeGroupUrl(url);

    private static bool OwnerAlive(string? owner)
    {
        var parts = owner?.Split(':');
        if (parts?.Length != 3 || !int.TryParse(parts[0], out var pid) || pid <= 0 || !long.TryParse(parts[1], out var ticks)) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private Task<int> UpdateQueuedAsync(FrayloDbContext db, FacebookPostTask task, string state, string reason, string message, CancellationToken ct)
    {
        if (task.State == state && task.ReasonCode == reason && task.Message == message) return Task.FromResult(0);
        var stamp = Now();
        var log = FacebookActivityLog.Append(task.ActivityLogJson, stamp, state == "Queued" ? "waiting" : state, message, reason);
        return db.FacebookPostTasks.Where(t => t.Id == task.Id && t.State == "Queued" && t.Revision == task.Revision)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, state).SetProperty(t => t.ReasonCode, reason)
                .SetProperty(t => t.Message, message).SetProperty(t => t.ActivityLogJson, log)
                .SetProperty(t => t.UpdatedAtUtc, stamp).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
    }
    private static async Task<FacebookTaskDto> ReadTaskAsync(FrayloDbContext db, string id, CancellationToken ct) =>
        (await TaskDtosAsync(db, [await db.FacebookPostTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("Không tìm thấy bài đăng.")], ct))[0];
    private static FacebookGroupDto GroupDto(FacebookTarget t) => new(t.Id, t.ProfileId, t.Name, t.Url);

    private static async Task<IReadOnlyList<FacebookTaskDto>> TaskDtosAsync(FrayloDbContext db, IEnumerable<FacebookPostTask> source, CancellationToken ct)
    {
        var tasks = source.ToList();
        var ids = tasks.Select(t => t.Id).ToList();
        var due = await db.FacebookTaskSchedules.AsNoTracking().Where(s => ids.Contains(s.TaskId)).ToDictionaryAsync(s => s.TaskId, s => s.DueAtUtc, ct);
        var media = (await db.FacebookPostTaskMedia.AsNoTracking().Where(m => ids.Contains(m.TaskId)).OrderBy(m => m.SortOrder).ToListAsync(ct))
            .GroupBy(m => m.TaskId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(m => m.AssetId).ToArray());
        return tasks.Select(t => new FacebookTaskDto(t.Id, t.BatchId, t.ProfileId, t.TargetId, t.GroupNameSnapshot, t.GroupUrlSnapshot, t.TextSnapshot, t.State, t.Message,
            t.CreatedAtUtc, t.UpdatedAtUtc, t.SubmittedAtUtc, t.ReasonCode, due.GetValueOrDefault(t.Id), media.GetValueOrDefault(t.Id) ?? [], FacebookActivityLog.Read(t.ActivityLogJson))).ToArray();
    }

    private static FacebookDraftDto DraftDto(FacebookDraft d) => new(d.Id, d.Name, d.ProfileId, d.Text,
        JsonSerializer.Deserialize<string[]>(d.TargetIdsJson) ?? [], JsonSerializer.Deserialize<string[]>(d.ImageAssetIdsJson) ?? [], d.UpdatedAtUtc, d.DueAtUtc, d.RewriteContent);

    // Giờ hẹn phải ở tương lai và không quá 1 năm; lưu UTC.
    private DateTimeOffset? ParseDueAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var due))
            throw new ArgumentException("Giờ hẹn không hợp lệ.");
        due = due.ToUniversalTime();
        if (due <= UtcNow) throw new ArgumentException("Giờ hẹn phải ở tương lai.");
        if (due > UtcNow.AddDays(365)) throw new ArgumentException("Giờ hẹn tối đa 365 ngày tới.");
        return due;
    }

    private static string ScheduledMessage(DateTimeOffset due) => $"Đã hẹn giờ đăng lúc {due.ToLocalTime():HH:mm dd/MM/yyyy}. Giữ Fraylo và máy hoạt động đến giờ hẹn.";
}
