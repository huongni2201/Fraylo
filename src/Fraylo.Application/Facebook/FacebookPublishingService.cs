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
    IBrowserProcessController controller)
{
    private sealed record TaskOutcome(string TaskId, string State, string Reason, string Message);
    private readonly Dictionary<string, TaskOutcome> _pendingOutcomes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly string _owner = $"{Environment.ProcessId}:{Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks}:{Guid.NewGuid():N}";
    private readonly string _startedAt = Now();
    private static readonly string[] ActiveStates = ["Queued", "Preparing", "Submitting", "NeedsUser"];
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    public async Task<FacebookListResponse> ListAsync(CancellationToken ct = default)
    {
        await using var db = new FrayloDbContext(dbOptions);
        var accounts = await db.BrowserProfiles.AsNoTracking().Where(p => p.Kind == "Facebook" && p.Lifecycle == "Active" && db.ProviderAccounts.Any(a => a.ProfileId == p.Id && a.ProviderKey == "facebook" && a.Lifecycle == "Active"))
            .OrderBy(p => p.DisplayName).Select(p => new FacebookAccountDto(p.Id, p.DisplayName ?? p.AccountIdentity ?? "Facebook", p.AccountIdentity, p.SessionStatus, db.ProviderAccounts.Any(a => a.ProfileId == p.Id && a.ProviderKey == "facebook" && a.Lifecycle == "Active" && a.Enabled))).ToListAsync(ct);
        var groups = await db.FacebookTargets.AsNoTracking().Where(t => t.Lifecycle == "Active").OrderBy(t => t.Name).ToListAsync(ct);
        var active = await db.FacebookPostTasks.AsNoTracking().Where(t => ActiveStates.Contains(t.State)).OrderBy(t => t.CreatedAtUtc).ToListAsync(ct);
        var history = await db.FacebookPostTasks.AsNoTracking().Where(t => !ActiveStates.Contains(t.State)).OrderByDescending(t => t.UpdatedAtUtc).Take(200).ToListAsync(ct);
        return new(accounts, groups.Select(GroupDto).ToArray(), active.Concat(history).Select(TaskDto).ToArray());
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
                var existing = await db.FacebookTargets.Where(t => t.ProfileId == profile.ProfileId).ToListAsync(ct);
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
        var batchId = batchGuid.ToString("N");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            request.AccountId,
            request.Text,
            TargetIds = request.TargetIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            request.ImageAssetIds
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
                return (await db.FacebookPostTasks.AsNoTracking().Where(t => t.BatchId == batchId).OrderBy(t => t.CreatedAtUtc).ToListAsync(ct)).Select(TaskDto).ToArray();
            }
            var profile = await ConnectedProfileAsync(request.AccountId, ct);
            var targets = await db.FacebookTargets.AsNoTracking().Where(t => request.TargetIds.Contains(t.Id) && t.ProfileId == request.AccountId && t.Lifecycle == "Active").ToListAsync(ct);
            if (targets.Count != request.TargetIds.Count) throw new ArgumentException("Có nhóm không thuộc tài khoản đã chọn hoặc không còn trong danh sách; hãy tải lại nhóm.");
            var assets = await db.Assets.AsNoTracking().Where(a => request.ImageAssetIds.Contains(a.Id) && a.AssetType == "Image" && a.Status == "Linked").ToListAsync(ct);
            if (assets.Count != request.ImageAssetIds.Count || assets.Any(a => a.Sha256Hash.Length != 64)) throw new ArgumentException("Ảnh đã chọn không còn khả dụng hoặc thiếu mã kiểm tra.");
            var now = Now();
            db.FacebookPostBatches.Add(new() { Id = batchId, ProfileId = profile.ProfileId, RequestHash = hash, CreatedAtUtc = now });
            var tasks = new List<FacebookPostTask>();
            foreach (var targetId in request.TargetIds)
            {
                var target = targets.Single(t => t.Id == targetId);
                var task = new FacebookPostTask
                {
                    Id = Guid.NewGuid().ToString("N"),
                    BatchId = batchId,
                    TargetId = target.Id,
                    ProfileId = profile.ProfileId,
                    AccountIdentitySnapshot = profile.AccountIdentity!,
                    GroupNameSnapshot = target.Name,
                    GroupUrlSnapshot = target.Url,
                    TextSnapshot = request.Text,
                    State = "Queued",
                    Message = "Đang chờ đăng; mỗi tài khoản cách ít nhất 5 phút, tối đa 20 bài/24 giờ.",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                tasks.Add(task); db.FacebookPostTasks.Add(task);
                for (var i = 0; i < request.ImageAssetIds.Count; i++)
                {
                    var asset = assets.Single(a => a.Id == request.ImageAssetIds[i]);
                    db.FacebookPostTaskMedia.Add(new() { TaskId = task.Id, AssetId = asset.Id, Sha256 = asset.Sha256Hash, SortOrder = i });
                }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return tasks.Select(TaskDto).ToArray();
        }
        finally { _changes.Release(); }
    }

    public async Task<FacebookTaskDto> CancelAsync(FacebookTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = new FrayloDbContext(dbOptions);
        var now = Now();
        await db.FacebookPostTasks.Where(t => t.Id == request.TaskId && (t.State == "Queued" || t.State == "NeedsUser" || t.State == "Preparing"))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Canceled").SetProperty(t => t.CancelRequested, true)
                .SetProperty(t => t.Message, "Đã hủy trước khi gửi lệnh đăng.").SetProperty(t => t.UpdatedAtUtc, now).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
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
        await db.FacebookPostTasks.Where(t => t.Id == task.Id && t.State == "NeedsUser" && t.SubmittedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Queued").SetProperty(t => t.ReasonCode, (string?)null)
                .SetProperty(t => t.Message, "Đã xác nhận tiếp tục; đang chờ lượt đăng.").SetProperty(t => t.UpdatedAtUtc, now).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
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
                task.State = "NeedsUser"; task.ReasonCode = "app_restarted";
                task.Message = "Ứng dụng đã khởi động lại; kiểm tra nội dung rồi chọn Tiếp tục nếu vẫn muốn đăng.";
            }
            else
            {
                if (OwnerAlive(task.LeaseOwnerId)) continue;
                task.State = task.SubmittedAtUtc != null || task.State == "Submitting" ? "Unknown" : "Failed";
                task.ReasonCode = "app_interrupted";
                task.Message = task.State == "Unknown" ? "Ứng dụng dừng sau khi bắt đầu gửi; hãy kiểm tra nhóm. Không tự đăng lại." : "Ứng dụng dừng trước khi gửi; chưa đăng và không tự thử lại.";
            }
            var stamp = Now();
            await db.FacebookPostTasks.Where(t => t.Id == task.Id && t.Revision == task.Revision && t.State == previousState)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, task.State).SetProperty(t => t.ReasonCode, task.ReasonCode)
                    .SetProperty(t => t.Message, task.Message).SetProperty(t => t.LeaseOwnerId, (string?)null).SetProperty(t => t.LeaseExpiresAtUtc, (string?)null)
                    .SetProperty(t => t.UpdatedAtUtc, stamp).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        if (!await _dispatch.WaitAsync(0, ct)) return false;
        try
        {
            foreach (var outcome in _pendingOutcomes.Values.ToArray()) await PersistOutcomeAsync(outcome);
            await using var db = new FrayloDbContext(dbOptions);
            var candidates = await db.FacebookPostTasks.AsNoTracking().Where(t => t.State == "Queued").OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.Id).ToListAsync(ct);
            var allProfiles = await profiles.ListProfilesAsync(ct);
            var enabledProfiles = await db.ProviderAccounts.AsNoTracking().Where(a => a.ProviderKey == "facebook" && a.Lifecycle == "Active" && a.Enabled).Select(a => a.ProfileId).ToListAsync(ct);
            foreach (var task in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var profile = allProfiles.SingleOrDefault(p => p.ProfileId == task.ProfileId);
                if (profile == null || profile.Kind != "Facebook" || profile.Lifecycle != "Active" || profile.SessionStatus != "Connected" || profile.AccountIdentity != task.AccountIdentitySnapshot || !enabledProfiles.Contains(task.ProfileId))
                {
                    await UpdateQueuedAsync(db, task.Id, "NeedsUser", "session_expired", "Hãy bật và xác minh đúng tài khoản Facebook trong Cài đặt, rồi chọn Tiếp tục.", ct);
                    continue;
                }
                if (controller.IsLoginWindowOpen(task.ProfileId) || profile.OperationalState == "Quiescing" || profile.VisibilityState != "Hidden") continue;
                if (await db.FacebookPostTasks.AnyAsync(t => t.ProfileId == task.ProfileId && (t.State == "Preparing" || t.State == "Submitting"), ct)) continue;
                if (!await HasBrowserBudgetAsync(db, task.ProfileId, ct)) continue;
                var eligible = await NextEligibleAtAsync(db, task.ProfileId, ct);
                if (eligible > DateTimeOffset.UtcNow)
                {
                    await UpdateQueuedAsync(db, task.Id, "Queued", "pacing", $"Chờ giãn cách/hạn mức; sớm nhất {eligible.ToLocalTime():HH:mm dd/MM/yyyy}.", ct);
                    continue;
                }
                var now = Now();
                int claimed;
                try
                {
                    claimed = await db.FacebookPostTasks.Where(t => t.Id == task.Id && t.State == "Queued" && !t.CancelRequested)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Preparing").SetProperty(t => t.LeaseOwnerId, _owner)
                            .SetProperty(t => t.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(15).ToString("O"))
                            .SetProperty(t => t.Message, "Đang kiểm tra nội dung và chuẩn bị bài đăng.").SetProperty(t => t.UpdatedAtUtc, now).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
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
            var result = await browser.PostGroupAsync(new(task.ProfileId, task.AccountIdentitySnapshot, task.GroupUrlSnapshot, task.GroupNameSnapshot, task.TextSnapshot, paths), async () =>
            {
                ct.ThrowIfCancellationRequested();
                var profile = await ConnectedProfileAsync(task.ProfileId, ct);
                if (profile.AccountIdentity != task.AccountIdentitySnapshot) throw new FacebookPostException("identity_mismatch", "Tài khoản đã đổi danh tính; chưa đăng.");
                await using var checkpoint = new FrayloDbContext(dbOptions);
                var stamp = Now();
                var changed = await checkpoint.FacebookPostTasks.Where(t => t.Id == task.Id && t.State == "Preparing" && !t.CancelRequested && t.LeaseOwnerId == _owner)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Submitting").SetProperty(t => t.SubmittedAtUtc, stamp)
                        .SetProperty(t => t.Message, "Đã bắt đầu gửi lệnh đăng; đang chờ Facebook xác nhận.").SetProperty(t => t.UpdatedAtUtc, stamp).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
                if (changed != 1) throw new OperationCanceledException("Bài đã hủy hoặc không còn quyền thực thi; chưa bấm Đăng.");
                submitted = true;
            }, ct);
            if (!submitted) throw new InvalidOperationException("Adapter không ghi checkpoint trước khi đăng.");
            state = result.Outcome == FacebookPostOutcome.PendingApproval ? "PendingApproval" : "Published";
            reason = "confirmed";
            message = state == "Published" ? "Facebook đã xác nhận xuất bản bài đăng." : "Chờ quản trị viên nhóm duyệt; bài chưa được xuất bản.";
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
            message = submitted ? "Không xác định được kết quả sau khi bắt đầu gửi. Kiểm tra nhóm trước khi tạo lô mới; không tự đăng lại." : ex is OperationCanceledException ? "Đã dừng trước khi gửi lệnh đăng." : "Không thể chuẩn bị bài đăng. Kiểm tra tài khoản, ảnh và kết nối rồi tạo yêu cầu mới nếu cần.";
        }
        finally { foreach (var file in files) await file.DisposeAsync(); }
        var outcome = new TaskOutcome(task.Id, state, reason, message);
        _pendingOutcomes[task.Id] = outcome;
        await PersistOutcomeAsync(outcome);
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
        var finalMessage = uncertain ? "Không xác định được kết quả sau khi bắt đầu gửi. Kiểm tra nhóm trước khi tạo lô mới; không tự đăng lại." : outcome.Message;
        var now = Now();
        await db.FacebookPostTasks.Where(t => t.Id == outcome.TaskId && t.LeaseOwnerId == _owner && (t.State == "Preparing" || t.State == "Submitting"))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, finalState).SetProperty(t => t.ReasonCode, outcome.Reason).SetProperty(t => t.Message, finalMessage)
                .SetProperty(t => t.LeaseOwnerId, (string?)null).SetProperty(t => t.LeaseExpiresAtUtc, (string?)null)
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

    public static async Task<DateTimeOffset> NextEligibleAtAsync(FrayloDbContext db, string profileId, CancellationToken ct = default)
    {
        var times = await db.FacebookPostTasks.AsNoTracking().Where(t => t.ProfileId == profileId && t.SubmittedAtUtc != null)
            .OrderByDescending(t => t.SubmittedAtUtc).Take(20).Select(t => t.SubmittedAtUtc!).ToListAsync(ct);
        if (times.Count == 0) return DateTimeOffset.MinValue;
        var stamps = times.Select(DateTimeOffset.Parse).OrderByDescending(t => t).ToArray();
        var next = stamps[0].AddMinutes(5);
        if (stamps.Length == 20 && stamps[19].AddHours(24) > next) next = stamps[19].AddHours(24);
        return next;
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

    private static Task<int> UpdateQueuedAsync(FrayloDbContext db, string id, string state, string reason, string message, CancellationToken ct) =>
        db.FacebookPostTasks.Where(t => t.Id == id && t.State == "Queued" && t.Message != message).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.State, state).SetProperty(t => t.ReasonCode, reason).SetProperty(t => t.Message, message).SetProperty(t => t.UpdatedAtUtc, Now()).SetProperty(t => t.Revision, t => t.Revision + 1), ct);
    private static async Task<FacebookTaskDto> ReadTaskAsync(FrayloDbContext db, string id, CancellationToken ct) =>
        TaskDto(await db.FacebookPostTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("Không tìm thấy bài đăng."));
    private static FacebookGroupDto GroupDto(FacebookTarget t) => new(t.Id, t.ProfileId, t.Name, t.Url);
    private static FacebookTaskDto TaskDto(FacebookPostTask t) => new(t.Id, t.BatchId, t.ProfileId, t.TargetId, t.GroupNameSnapshot, t.GroupUrlSnapshot, t.TextSnapshot, t.State, t.Message, t.CreatedAtUtc, t.UpdatedAtUtc, t.SubmittedAtUtc);
}
