using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Facebook;
using Fraylo.Browser;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Publishing;
using Fraylo.Contracts.Facebook;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;
using Fraylo.Infrastructure.Storage;

if (args.FirstOrDefault() == "rewrite") return await FacebookRewriteSmokeTests.RunAsync();

var root = Path.Combine(Path.GetTempPath(), "Fraylo_facebook_application_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var database = Path.Combine(root, "test.db");
await new SqliteDatabaseBootstrapper().BootstrapAsync(database);
var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite(DatabasePathHelper.BuildConnectionString(database)).AddInterceptors(new SqlitePragmaInterceptor()).Options;
var registry = new ProfileRegistry(() => new FrayloDbContext(options), Path.Combine(root, "profiles"));
var roots = new StorageRootRegistry(options, root);
await roots.EnsureDefaultRootsAsync();
var browser = new FakeBrowser();
var controller = new FakeController();
var service = new FacebookPublishingService(options, roots, registry, browser, controller);
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS " + name); }
async Task Throws(Func<Task> action, string name)
{
    var failed = false;
    try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { failed = true; }
    Check(failed, name);
}
async Task<FacebookTaskDto> TaskById(string id) => (await service.ListAsync()).Tasks.Single(t => t.TaskId == id);
async Task ResetWork()
{
    await using var db = new FrayloDbContext(options);
    await db.FacebookTaskSchedules.ExecuteDeleteAsync(); await db.FacebookPostTaskMedia.ExecuteDeleteAsync(); await db.FacebookPostTasks.ExecuteDeleteAsync(); await db.FacebookPostBatches.ExecuteDeleteAsync();
    await db.BrowserProfiles.ExecuteUpdateAsync(s => s.SetProperty(p => p.SessionStatus, "Connected").SetProperty(p => p.OperationalState, "Stopped"));
    await db.AppSettings.Where(s => s.Key == "scheduler.max_open_browsers").ExecuteDeleteAsync();
    await db.ProviderAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.Enabled, true));
    browser.Post = null; browser.Calls = 0; browser.DiscoverError = null;
}
FacebookCreateBatchRequest Request(string account, IEnumerable<string> ids, string text = "Bài đăng 😀\nDòng hai", string[]? images = null) =>
    new(Guid.NewGuid().ToString(), account, ids.ToArray(), text, images ?? []);
try
{
    await using (var db = new FrayloDbContext(options))
    {
        foreach (var id in new[] { "p1", "p2" }) db.BrowserProfiles.Add(new() { Id = id, Kind = "Facebook", AccountIdentity = "Facebook (" + (id == "p1" ? "1" : "2") + ")", ProviderKey = "facebook", SessionStatus = "Connected", CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"), UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync();
        foreach (var id in new[] { "p1", "p2" }) db.ProviderAccounts.Add(new() { Id = "account-" + id, ProfileId = id, ProviderKey = "facebook", Enabled = true, DisplayName = "Facebook test", CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"), UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync();
    }
    var poster = new FakePersonalPoster();
    var personalService = new FacebookPublishingService(options, roots, registry, browser, controller, personalPoster: poster);
    var personalRequest = Request("p1", ["personal:p1"]) with { TargetKind = "Profile" };
    var personalTask = (await personalService.CreateBatchAsync(personalRequest))[0];
    Check((await personalService.CreateBatchAsync(personalRequest))[0].TaskId == personalTask.TaskId, "Cá nhân: replay không tạo bài trùng");
    await Throws(async () => await personalService.CreateBatchAsync(personalRequest with { BatchRequestId = Guid.NewGuid().ToString(), TargetIds = ["personal:p2"] }), "Cá nhân: từ chối trang cá nhân tài khoản khác");
    await Throws(async () => await personalService.CreateBatchAsync(personalRequest with { BatchRequestId = Guid.NewGuid().ToString(), TargetKind = "Group" }), "Không nhận trang cá nhân như nhóm");
    Check(await personalService.ProcessNextAsync() && poster.Calls == 1 && browser.Calls == 0, "Cá nhân: dùng adapter cá nhân qua hàng đợi");
    Check((await personalService.ListAsync()).Tasks.Single(t => t.TaskId == personalTask.TaskId).State == "Published", "Cá nhân: lưu kết quả Published");
    await personalService.CreateBatchAsync(personalRequest with { BatchRequestId = Guid.NewGuid().ToString() });
    Check(!await personalService.ProcessNextAsync() && poster.Calls == 1, "Cá nhân: giữ giãn cách theo tài khoản");
    await ResetWork();
    poster.Unknown = true;
    personalTask = (await personalService.CreateBatchAsync(personalRequest with { BatchRequestId = Guid.NewGuid().ToString() }))[0];
    Check(await personalService.ProcessNextAsync(), "Cá nhân: xử lý kết quả chưa rõ");
    Check((await personalService.ListAsync()).Tasks.Single(t => t.TaskId == personalTask.TaskId).State == "Unknown", "Cá nhân: không báo thành công khi chưa có bằng chứng");
    var calls = poster.Calls;
    Check(!await personalService.ProcessNextAsync() && poster.Calls == calls, "Cá nhân: Unknown không tự gửi lại");
    await ResetWork();
    browser.Discovery = new([new("a", "Nhóm A", "https://web.facebook.com/groups/a/?ref=tracking"), new("a", "Nhóm A", "https://www.facebook.com/groups/a/"), new("b", "Nhóm B", "https://www.facebook.com/groups/b/")], true, "Đủ nhóm");
    var sync = await service.SyncGroupsAsync(new("p1"));
    Check(sync.Groups.Count == 2 && sync.Groups.All(g => g.Url.StartsWith("https://www.facebook.com/groups/")), "Sync chuẩn hóa URL, gộp trùng");
    var p1 = sync.Groups.ToArray();
    var other = await service.SyncGroupsAsync(new("p2"));
    Check(other.Groups.Count == 2 && other.Groups.All(g => !p1.Any(x => x.TargetId == g.TargetId)), "Hai tài khoản cùng nhóm có đích riêng");
    browser.Discovery = new([new("a", "Nhóm A", "https://www.facebook.com/groups/a/")], false, "Chưa đủ");
    Check((await service.SyncGroupsAsync(new("p1"))).Groups.Count == 2, "Sync một phần không xóa nhóm chưa tải tới");
    browser.Discovery = browser.Discovery with { IsComplete = true };
    Check((await service.SyncGroupsAsync(new("p1"))).Groups.Count == 1, "Sync đủ chỉ archive nhóm đã rời");
    browser.Discovery = new([new("a", "Nhóm A", "https://www.facebook.com/groups/a/"), new("b", "Nhóm B", "https://www.facebook.com/groups/b/")], true, "Đủ");
    p1 = (await service.SyncGroupsAsync(new("p1"))).Groups.ToArray();
    foreach (var url in new[] { "javascript:alert(1)", "https://facebook.com.evil.test/groups/a/", "https://x@facebook.com/groups/a/", "https://facebook.com/groups/feed/", "https://facebook.com/groups/a/posts/1/" })
        await Throws(() => Task.Run(() => FacebookPublishingService.NormalizeGroupUrl(url)), "Từ chối URL lạ: " + url);

    await ResetWork();
    var clock = new TestClock(DateTimeOffset.UtcNow.AddDays(1));
    await using (var db = new FrayloDbContext(options))
    {
        db.FacebookAnalyticsPages.Add(new() { Id = "analytics-page-test", ProfileId = "p1", FacebookPageId = "100", Name = "Page test" });
        db.FacebookAnalyticsCaptures.Add(new() { Id = "analytics-busy", PageId = "analytics-page-test", StartedAtUtc = DateTimeOffset.UtcNow.ToString("O"), Owner = "test" });
        await db.SaveChangesAsync();
    }
    var waitingForAnalytics = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId]))).Single();
    Check(!await service.ProcessNextAsync() && browser.Calls == 0 && (await TaskById(waitingForAnalytics.TaskId)).State == "Queued", "Analytics đang đọc thì bài đăng cùng profile giữ Queued, không Failed");
    await service.ProcessNextAsync();
    var waiting = await TaskById(waitingForAnalytics.TaskId);
    Check(waiting.ReasonCode == "analytics_running" && waiting.ActivityLog?.Count(e => e.ReasonCode == "analytics_running") == 1,
        "Lý do chờ Analytics được lưu một lần qua nhiều lần quét");
    await service.CancelAsync(new(waitingForAnalytics.TaskId));
    await using (var db = new FrayloDbContext(options))
    {
        await db.FacebookAnalyticsCaptures.Where(c => c.Id == "analytics-busy").ExecuteDeleteAsync();
        await db.FacebookAnalyticsPages.Where(p => p.Id == "analytics-page-test").ExecuteDeleteAsync();
    }
    await ResetWork();
    var timed = new FacebookPublishingService(options, roots, registry, browser, controller, clock);

    browser.Discovery = new(Enumerable.Range(1, 50).Select(i => new FacebookJoinedGroup("g" + i, "Nhóm " + i, "https://www.facebook.com/groups/g" + i + "/")).ToArray(), true, "Đủ");
    var fifty = (await timed.SyncGroupsAsync(new("p1"))).Groups;
    var firstIds = fifty.Take(20).Select(g => g.TargetId).ToArray();
    var secondIds = fifty.Skip(20).Select(g => g.TargetId).ToArray();
    var dueA = clock.GetUtcNow().AddHours(1);
    var a = await timed.CreateBatchAsync(Request("p1", firstIds, "Bài A") with { Name = "Bài A", DueAtUtc = dueA.ToString("O") });
    var b = await timed.CreateBatchAsync(Request("p1", secondIds, "Bài B") with { Name = "Bài B", DueAtUtc = dueA.AddDays(1).ToString("O") });
    Check(!await timed.ProcessNextAsync() && browser.Calls == 0, "Chưa đến giờ không mở browser");
    var posts = (await timed.ListAsync()).Posts!;
    Check(posts.Count == 2 && posts.Single(p => p.Name == "Bài A").Tasks.Count == 20 && posts.Single(p => p.Name == "Bài B").Tasks.Count == 30, "Hai bài 20/30 nhóm lưu độc lập");
    var pa = posts.Single(p => p.Name == "Bài A");
    var paused = await timed.PostActionAsync(new(pa.BatchId, pa.Revision, "pause"));
    await Throws(async () => await timed.PostActionAsync(new(pa.BatchId, pa.Revision, "cancel")), "Revision cũ không được hủy bài");
    clock.Current = dueA;
    Check(!await timed.ProcessNextAsync(), "Pause chặn dispatch dù đã đến giờ");
    await timed.PostActionAsync(new(paused.BatchId, paused.Revision, "resume"));
    for (var i = 0; i < 20; i++)
    {
        Check(await timed.ProcessNextAsync(), "Bài A gửi nhóm " + (i + 1));
        Check(!await timed.ProcessNextAsync(), "Không gửi dồn trong cùng mốc giờ");
        clock.Current = clock.Current.AddSeconds(299);
        Check(!await timed.ProcessNextAsync(), "Chưa đủ 5 phút thì chưa gửi");
        clock.Current = clock.Current.AddSeconds(31); // 5 phút 30 giây: đủ cho mọi độ lệch 0–30 giây
    }
    var gaps = Enumerable.Range(0, 200).Select(i => FacebookPublishingService.PacingGap("task-" + i)).ToArray();
    Check(gaps.All(g => g >= TimeSpan.FromMinutes(5) && g <= TimeSpan.FromSeconds(330)) && gaps.Distinct().Count() > 50, "Giãn cách 5 phút + 0–30 giây, có phân tán");
    Check(FacebookPublishingService.PacingGap("task-1") == FacebookPublishingService.PacingGap("task-1"), "Độ lệch ổn định theo lượt gửi trước");
    var publishedA = (await timed.ListAsync()).Posts!.Single(p => p.Name == "Bài A");
    Check(publishedA.Tasks.Select(t => t.TargetId).SequenceEqual(firstIds), "Giữ đúng thứ tự nhóm đã xác nhận");
    Check(DateTimeOffset.Parse(publishedA.Tasks.Last().SubmittedAtUtc!) == dueA.AddSeconds(19 * 330), "20 nhóm, mỗi lượt cách 5 phút 30 giây (đủ độ lệch tối đa)");
    clock.Current = dueA.AddDays(1);
    for (var i = 0; i < 30; i++) { Check(await timed.ProcessNextAsync(), "Bài B gửi nhóm " + (i + 1)); clock.Current = clock.Current.AddSeconds(330); }
    var publishedB = (await timed.ListAsync()).Posts!.Single(p => p.Name == "Bài B");
    Check(DateTimeOffset.Parse(publishedB.Tasks.Last().SubmittedAtUtc!) == dueA.AddDays(1).AddSeconds(29 * 330), "30 nhóm không bị quota chặn");
    var overlap1 = await timed.CreateBatchAsync(Request("p1", firstIds.Take(2), "Trùng A"));
    var overlap2 = await timed.CreateBatchAsync(Request("p1", secondIds.Take(2), "Trùng B"));
    Check(await timed.ProcessNextAsync() && !await timed.ProcessNextAsync(), "Hai bài cùng giờ cùng profile vẫn cách 5 phút");
    var independent = await timed.CreateBatchAsync(Request("p2", [other.Groups[0].TargetId], "Tài khoản khác"));
    Check(await timed.ProcessNextAsync() && (await timed.ListAsync()).Tasks.Single(t => t.TaskId == independent[0].TaskId).State == "Published", "Cooldown p1 không chặn p2");
    clock.Current = clock.Current.AddSeconds(330);
    var overlapPosts = (await timed.ListAsync()).Posts!.Where(p => p.BatchId == overlap1[0].BatchId || p.BatchId == overlap2[0].BatchId).ToArray();
    var preSubmitPost = overlapPosts.Single(p => p.Tasks.Any(t => t.State == "Published"));
    var pauseAttempted = false;
    browser.Post = async (_, before) =>
    {
        pauseAttempted = true;
        await timed.PostActionAsync(new(preSubmitPost.BatchId, preSubmitPost.Revision, "pause"));
        await before(); throw new InvalidOperationException("Không được click sau pause");
    };
    await timed.ProcessNextAsync();
    Check(pauseAttempted && (await timed.ListAsync()).Posts!.Single(p => p.BatchId == preSubmitPost.BatchId).Tasks.Any(t => t.State == "Queued"), "Pause trong Preparing trả lượt về Queued, checkpoint không gửi");
    browser.Post = null;
    var otherPost = overlapPosts.Single(p => p.BatchId != preSubmitPost.BatchId);
    await timed.PostActionAsync(new(otherPost.BatchId, otherPost.Revision, "cancel"));
    Check((await timed.ListAsync()).Posts!.Single(p => p.BatchId == otherPost.BatchId).Tasks.All(t => t.State == "Canceled"), "Hủy nguyên bài chỉ hủy phần chưa gửi");
    var scheduledDraft = await timed.SaveDraftAsync(new(null, "Nháp có lịch", "p1", "x", firstIds, [], clock.Current.AddDays(1).ToString("O")));
    Check((await timed.ListAsync()).Drafts!.Single(d => d.DraftId == scheduledDraft.DraftId).DueAtUtc == scheduledDraft.DueAtUtc, "Nháp lưu cả giờ hẹn");
    await timed.DeleteDraftAsync(new(scheduledDraft.DraftId));
    await ResetWork();
    browser.Discovery = new([new("a", "Nhóm A", "https://www.facebook.com/groups/a/"), new("b", "Nhóm B", "https://www.facebook.com/groups/b/")], true, "Đủ");
    p1 = (await service.SyncGroupsAsync(new("p1"))).Groups.ToArray();
    var request = Request("p1", p1.Select(g => g.TargetId));
    var tasks = await service.CreateBatchAsync(request);
    var replay = await service.CreateBatchAsync(request);
    Check(tasks.Count == 2 && replay.Select(t => t.TaskId).Order().SequenceEqual(tasks.Select(t => t.TaskId).Order()), "Replay cùng batch không tạo thêm tác vụ");
    await Throws(async () => { await service.CreateBatchAsync(request with { Text = "Nội dung khác" }); }, "Replay khác nội dung bị chặn");
    await Throws(async () => { await service.CreateBatchAsync(request with { AccountId = "p2" }); }, "Replay khác tài khoản bị chặn");
    await Throws(async () => { await service.CreateBatchAsync(Request("p2", [p1[0].TargetId])); }, "Không đăng vào đích thuộc tài khoản khác");
    await using (var db = new FrayloDbContext(options))
    {
        await db.FacebookTargets.Where(t => t.Id == p1[0].TargetId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, "Đã đổi tên"));
    }
    Check((await TaskById(tasks[0].TaskId)).Text == request.Text && (await TaskById(tasks[0].TaskId)).GroupName != "Đã đổi tên", "Tác vụ giữ bản chụp nội dung/tên nhóm");
    Check(await service.ProcessNextAsync(), "Queue xử lý một bài");
    Check((await service.ListAsync()).Tasks.Count(t => t.State == "Published") == 1 && browser.Calls == 1, "Published chỉ sau callback checkpoint");
    Check(!await service.ProcessNextAsync() && browser.Calls == 1, "Chặn bài tiếp cùng profile trong 5 phút");
    var queued = (await service.ListAsync()).Tasks.Single(t => t.State == "Queued");
    Check((await service.CancelAsync(new(queued.TaskId))).State == "Canceled", "Hủy bài queued");

    await ResetWork();
    var cancelTask = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId])))[0];
    var clicked = false;
    browser.Post = async (_, before) => { await service.CancelAsync(new(cancelTask.TaskId)); await before(); clicked = true; return new(FacebookPostOutcome.Published, null, null); };
    await service.ProcessNextAsync();
    var canceledBeforeSubmit = await TaskById(cancelTask.TaskId);
    Check(!clicked && canceledBeforeSubmit.State == "Canceled" && canceledBeforeSubmit.ActivityLog?.Last().Code == "cancel", "Hủy trong Preparing làm CAS 0 dòng, không click");

    await ResetWork();
    var unknown = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId])))[0];
    browser.Post = async (input, before) =>
    {
        await input.ReportStage!("opening_composer", "Đang mở khung soạn bài.");
        var running = await TaskById(unknown.TaskId);
        Check(running.State == "Preparing" && running.ActivityLog?.Last().Code == "opening_composer", "Thấy bước hiện tại trong lúc đang chuẩn bị");
        await before();
        throw new IOException("private-path-should-not-be-logged");
    };
    await service.ProcessNextAsync();
    Check((await TaskById(unknown.TaskId)).State == "Unknown" && !await service.ProcessNextAsync(), "Mất kết nối sau checkpoint -> Unknown, không tự retry");
    var unknownLog = (await TaskById(unknown.TaskId)).ActivityLog!;
    Check(unknownLog.Select(e => e.Code).SequenceEqual(["queued", "checking_input", "opening_browser", "opening_composer", "submit_checkpoint", "Unknown"]) &&
        unknownLog[^1].ReasonCode == "outcome_unknown" && !unknownLog.Any(e => e.Message.Contains("private-path")),
        "Nhật ký bền vững giữ đúng mốc gửi và lỗi, bỏ exception thô");
    await Throws(async () => { await service.ResumeAsync(new(unknown.TaskId)); }, "Không resume Unknown để tránh đăng trùng");

    await ResetWork();
    var pending = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId])))[0];
    browser.Post = async (_, before) => { await before(); return new(FacebookPostOutcome.PendingApproval, null, "pending"); };
    await service.ProcessNextAsync();
    Check((await TaskById(pending.TaskId)).State == "PendingApproval", "Group chờ duyệt không báo Published");

    await ResetWork();
    var needs = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId])))[0];
    browser.Post = (_, _) => throw new FacebookNeedsUserException("checkpoint", "Cần xác minh");
    await service.ProcessNextAsync();
    Check((await TaskById(needs.TaskId)).State == "NeedsUser" && (await registry.GetProfileAsync("p1"))!.SessionStatus == "Unverified", "Checkpoint đưa bài/account đúng sang NeedsUser/Unverified");
    await Throws(async () => { await service.ResumeAsync(new(needs.TaskId)); }, "Không resume khi phiên chưa xác minh");
    await registry.UpdateProfileStateAsync("p1", sessionStatus: "Connected");
    Check((await service.ResumeAsync(new(needs.TaskId))).State == "Queued", "Người dùng tiếp tục sau xác minh");

    await ResetWork();
    var expired = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId])))[0];
    await registry.UpdateProfileStateAsync("p1", sessionStatus: "Unverified");
    await service.ProcessNextAsync();
    Check(browser.Calls == 0 && (await TaskById(expired.TaskId)).State == "NeedsUser", "Phiên mất trước dispatch không mở Facebook");

    await ResetWork();
    var disabled = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId])))[0];
    await using (var db = new FrayloDbContext(options)) await db.ProviderAccounts.Where(a => a.ProfileId == "p1").ExecuteUpdateAsync(s => s.SetProperty(a => a.Enabled, false));
    Check(!(await service.ListAsync()).Accounts.Single(a => a.AccountId == "p1").Enabled, "Danh sách trả trạng thái Enabled=false, không sửa trạng thái phiên");
    await Throws(async () => { await service.CreateBatchAsync(Request("p1", [p1[0].TargetId])); }, "Tài khoản tắt không tạo lô mới");
    await service.ProcessNextAsync();
    Check(browser.Calls == 0 && (await TaskById(disabled.TaskId)).State == "NeedsUser", "Tài khoản tắt sau khi tạo lô không được dispatch");

    await ResetWork();
    var image = Path.Combine(StorageRootRegistry.GetDefaultRootPath(StorageRootRegistry.RootDefaultImagesId, root), "probe.png");
    Directory.CreateDirectory(Path.GetDirectoryName(image)!);
    await File.WriteAllBytesAsync(image, [1, 2, 3]);
    await using (var db = new FrayloDbContext(options))
    {
        db.Assets.Add(new() { Id = "image1", StorageRootId = StorageRootRegistry.RootDefaultImagesId, RelativePath = "probe.png", AssetType = "Image", Sha256Hash = Convert.ToHexString(SHA256.HashData([1, 2, 3])), ByteSize = 3, CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync();
    }
    var stale = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId], images: ["image1"])))[0];
    await File.WriteAllBytesAsync(image, [9, 9, 9]);
    await service.ProcessNextAsync();
    Check(browser.Calls == 0 && (await TaskById(stale.TaskId)).State == "Failed", "Ảnh đổi hash -> Failed trước khi mở Facebook");

    await ResetWork();
    var budget = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId])))[0];
    await using (var db = new FrayloDbContext(options))
    {
        db.AppSettings.Add(new() { Key = "scheduler.max_open_browsers", Value = "1", UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") });
        await db.BrowserProfiles.Where(p => p.Id == "p2").ExecuteUpdateAsync(s => s.SetProperty(p => p.OperationalState, "Running"));
        await db.SaveChangesAsync();
    }
    Check(!await service.ProcessNextAsync() && browser.Calls == 0 && (await TaskById(budget.TaskId)).State == "Queued", "Ngân sách browser đầy giữ Queued");

    await Throws(async () => { await service.SyncGroupsAsync(new("p1")); }, "Đồng bộ nhóm cũng tuân thủ ngân sách browser");

    await ResetWork();
    var many = await service.CreateBatchAsync(Request("p1", [p1[0].TargetId]));
    await using (var db = new FrayloDbContext(options))
    {
        var batch = await db.FacebookPostBatches.SingleAsync();
        for (var i = 0; i < 20; i++) db.FacebookPostTasks.Add(new() { Id = Guid.NewGuid().ToString("N"), BatchId = batch.Id, TargetId = p1[0].TargetId, ProfileId = "p1", AccountIdentitySnapshot = "Facebook (1)", GroupNameSnapshot = "A", GroupUrlSnapshot = p1[0].Url, TextSnapshot = "đã đăng", State = "Published", SubmittedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-6 - i).ToString("O"), CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-6 - i).ToString("O"), UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync();
    }
    Check(await service.ProcessNextAsync() && browser.Calls == 1, "Lượt 21 vẫn đăng sau 5 phút, không còn quota ngày");

    await ResetWork();
    var lockedResult = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId])))[0];
    using (var locker = new SqliteConnection(DatabasePathHelper.BuildConnectionString(database)))
    {
        await locker.OpenAsync();
        SqliteTransaction? held = null;
        browser.Post = async (_, before) => { await before(); held = locker.BeginTransaction(); return new(FacebookPostOutcome.Published, null, "published"); };
        var wasBusy = false;
        try { await service.ProcessNextAsync(); } catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { wasBusy = true; }
        held?.Rollback(); held?.Dispose();
        Check(wasBusy && !await service.ProcessNextAsync() && browser.Calls == 1 && (await TaskById(lockedResult.TaskId)).State == "Published", "SQLite bận khi lưu kết quả: lần quét sau chỉ ghi Published, không gửi lại");
    }

    await ResetWork();
    var recover = await service.CreateBatchAsync(Request("p1", p1.Select(g => g.TargetId)));
    var third = (await service.CreateBatchAsync(Request("p2", [other.Groups[0].TargetId])))[0];
    await using (var db = new FrayloDbContext(options))
    {
        await db.FacebookPostTasks.Where(t => t.Id == recover[0].TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Submitting").SetProperty(t => t.SubmittedAtUtc, DateTimeOffset.UtcNow.ToString("O")).SetProperty(t => t.LeaseOwnerId, "0:0:dead"));
        await db.FacebookPostTasks.Where(t => t.Id == third.TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.State, "Preparing").SetProperty(t => t.LeaseOwnerId, "0:0:dead"));
    }
    var restarted = new FacebookPublishingService(options, roots, registry, browser, controller);
    await restarted.RecoverInterruptedAsync();
    var states = (await restarted.ListAsync()).Tasks.ToDictionary(t => t.TaskId, t => t.State);
    Check(states[recover[0].TaskId] == "Unknown" && states[recover[1].TaskId] == "NeedsUser" && states[third.TaskId] == "Failed", "Recovery: Submitting Unknown, Queued chờ xác nhận, Preparing lỗi; không tự gửi");

    await ResetWork();
    browser.DiscoverError = new FacebookNeedsUserException("session_expired", "Phiên hết hạn");
    var syncExpired = false;
    try { await service.SyncGroupsAsync(new("p1")); } catch (FacebookNeedsUserException) { syncExpired = true; }
    Check(syncExpired && (await registry.GetProfileAsync("p1"))!.SessionStatus == "Unverified", "Sync hết phiên đổi account sang Unverified để hiện nút đăng nhập");

    // ===== Hẹn giờ =====
    string Due(TimeSpan offset) => DateTimeOffset.UtcNow.Add(offset).ToString("O");
    async Task SetDue(string taskId, TimeSpan offset) { await using var d = new FrayloDbContext(options); await d.FacebookTaskSchedules.Where(s => s.TaskId == taskId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DueAtUtc, Due(offset))); }

    await ResetWork();
    await Throws(async () => { await service.CreateBatchAsync(Request("p1", [p1[0].TargetId]) with { DueAtUtc = Due(TimeSpan.FromMinutes(-1)) }); }, "Giờ hẹn trong quá khứ bị từ chối");
    await Throws(async () => { await service.CreateBatchAsync(Request("p1", [p1[0].TargetId]) with { DueAtUtc = Due(TimeSpan.FromDays(400)) }); }, "Giờ hẹn quá 365 ngày bị từ chối");
    await Throws(async () => { await service.CreateBatchAsync(Request("p1", [p1[0].TargetId]) with { DueAtUtc = "không phải giờ" }); }, "Giờ hẹn sai định dạng bị từ chối");
    var scheduledRequest = Request("p1", [p1[0].TargetId]) with { DueAtUtc = Due(TimeSpan.FromHours(1)) };
    var scheduled = (await service.CreateBatchAsync(scheduledRequest))[0];
    Check(scheduled.State == "Queued" && scheduled.DueAtUtc != null && scheduled.Message.Contains("hẹn giờ"), "Tạo lô hẹn giờ: Queued kèm DueAtUtc");
    Check((await service.CreateBatchAsync(scheduledRequest))[0].TaskId == scheduled.TaskId, "Replay lô hẹn giờ cùng giờ trả lô cũ");
    await Throws(async () => { await service.CreateBatchAsync(scheduledRequest with { DueAtUtc = Due(TimeSpan.FromHours(2)) }); }, "Replay khác giờ hẹn bị chặn");
    Check(!await service.ProcessNextAsync() && browser.Calls == 0 && (await TaskById(scheduled.TaskId)).State == "Queued", "Chưa tới giờ hẹn thì không mở Facebook");
    await registry.UpdateProfileStateAsync("p1", sessionStatus: "Unverified");
    await service.ProcessNextAsync();
    Check((await TaskById(scheduled.TaskId)).State == "Queued", "Chưa tới giờ hẹn thì chưa kiểm phiên, không chuyển NeedsUser");
    await registry.UpdateProfileStateAsync("p1", sessionStatus: "Connected");
    await SetDue(scheduled.TaskId, TimeSpan.FromSeconds(-1));
    Check(await service.ProcessNextAsync() && browser.Calls == 1 && (await TaskById(scheduled.TaskId)).State == "Published", "Tới giờ hẹn thì đăng");

    await ResetWork();
    var postNow = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId]) with { DueAtUtc = Due(TimeSpan.FromHours(3)) }))[0];
    var nowResult = await service.PostNowAsync(new(postNow.TaskId));
    Check(nowResult.DueAtUtc == null && nowResult.State == "Queued", "Đăng ngay bỏ giờ hẹn");
    Check(await service.ProcessNextAsync() && (await TaskById(postNow.TaskId)).State == "Published", "Bài đăng ngay được xử lý");
    await Throws(async () => { await service.PostNowAsync(new(postNow.TaskId)); }, "Không Đăng ngay bài đã xử lý");

    await ResetWork();
    var moving = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId]) with { DueAtUtc = Due(TimeSpan.FromHours(1)) }))[0];
    var newDue = Due(TimeSpan.FromHours(5));
    var moved = await service.RescheduleAsync(new(moving.TaskId, newDue));
    Check(moved.State == "Queued" && DateTimeOffset.Parse(moved.DueAtUtc!) > DateTimeOffset.UtcNow.AddHours(4), "Đổi giờ hẹn cập nhật DueAtUtc");
    await Throws(async () => { await service.RescheduleAsync(new(moving.TaskId, Due(TimeSpan.FromMinutes(-5)))); }, "Đổi giờ về quá khứ bị từ chối");
    var immediate = (await service.CreateBatchAsync(Request("p1", [p1[1].TargetId])))[0];
    Check((await service.RescheduleAsync(new(immediate.TaskId, Due(TimeSpan.FromHours(2))))).DueAtUtc != null, "Bài xếp hàng ngay có thể chuyển sang hẹn giờ");
    await service.CancelAsync(new(moving.TaskId));
    await Throws(async () => { await service.RescheduleAsync(new(moving.TaskId, Due(TimeSpan.FromHours(2)))); }, "Không đổi giờ bài đã hủy");

    await ResetWork();
    var future = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId]) with { DueAtUtc = Due(TimeSpan.FromHours(4)) }))[0];
    var overdue = (await service.CreateBatchAsync(Request("p1", [p1[1].TargetId]) with { DueAtUtc = Due(TimeSpan.FromHours(1)) }))[0];
    await SetDue(overdue.TaskId, TimeSpan.FromMinutes(-30));
    var afterRestart = new FacebookPublishingService(options, roots, registry, browser, controller);
    await afterRestart.RecoverInterruptedAsync();
    var recovered2 = (await afterRestart.ListAsync()).Tasks.ToDictionary(t => t.TaskId);
    Check(recovered2[future.TaskId].State == "Queued" && recovered2[overdue.TaskId].State == "NeedsUser" && recovered2[overdue.TaskId].ReasonCode == "schedule_missed", "Khởi động lại: hẹn giờ tương lai giữ nguyên, quá hạn thành lỡ lịch, không đăng muộn");
    var rescheduledMissed = await afterRestart.RescheduleAsync(new(overdue.TaskId, Due(TimeSpan.FromHours(6))));
    Check(rescheduledMissed.State == "Queued" && rescheduledMissed.ReasonCode == null && rescheduledMissed.DueAtUtc != null, "Lỡ lịch: Đổi giờ đưa về hàng đợi");
    await SetDue(overdue.TaskId, TimeSpan.FromMinutes(-30));
    var secondRestart = new FacebookPublishingService(options, roots, registry, browser, controller);
    await secondRestart.RecoverInterruptedAsync();
    var missedAgain = await secondRestart.ResumeAsync(new(overdue.TaskId));
    Check(missedAgain.State == "Queued", "Lỡ lịch: Đăng ngay (Tiếp tục) đưa về hàng đợi");
    Check(browser.Calls == 0, "Recovery và đổi giờ không gọi Facebook");

    // ===== Hình ảnh trong DTO =====
    await ResetWork();
    var withImage = (await service.CreateBatchAsync(Request("p1", [p1[0].TargetId], images: ["image1"])))[0];
    Check(withImage.ImageAssetIds!.SequenceEqual(["image1"]) && (await TaskById(withImage.TaskId)).ImageAssetIds!.SequenceEqual(["image1"]), "DTO trả danh sách ảnh để đăng lại");

    // ===== Nháp =====
    await using (var db = new FrayloDbContext(options)) await db.FacebookDrafts.ExecuteDeleteAsync();
    var draft = await service.SaveDraftAsync(new(null, "  Nháp 1  ", "p1", "Nội dung 😀", [p1[0].TargetId, p1[1].TargetId], ["image1"]));
    Check(draft.Name == "Nháp 1" && draft.TargetIds.Count == 2 && draft.ImageAssetIds.SequenceEqual(["image1"]) && (await service.ListAsync()).Drafts!.Count == 1, "Lưu nháp bền");
    var edited = await service.SaveDraftAsync(new(draft.DraftId, "Nháp 1", "p1", "Sửa", [p1[0].TargetId], []));
    var listed = (await service.ListAsync()).Drafts!;
    Check(listed.Count == 1 && listed[0].Text == "Sửa" && listed[0].TargetIds.Count == 1 && edited.DraftId == draft.DraftId, "Sửa nháp ghi đè, không tạo thêm");
    await Throws(async () => { await service.SaveDraftAsync(new(null, "", "p1", "x", [], [])); }, "Nháp thiếu tên bị từ chối");
    await Throws(async () => { await service.SaveDraftAsync(new(null, "n", "p1", "x", ["a", "a"], [])); }, "Nháp lặp nhóm bị từ chối");
    await Throws(async () => { await service.SaveDraftAsync(new(null, "n", "p1", "x", [], Enumerable.Range(0, 11).Select(i => "i" + i).ToArray())); }, "Nháp quá 10 ảnh bị từ chối");
    var restartedForDrafts = new FacebookPublishingService(options, roots, registry, browser, controller);
    Check((await restartedForDrafts.ListAsync()).Drafts!.Single().Text == "Sửa", "Nháp còn sau khi khởi động lại (SQLite)");
    for (var i = 0; i < 49; i++) await service.SaveDraftAsync(new(null, "n" + i, "p1", "x", [], []));
    await Throws(async () => { await service.SaveDraftAsync(new(null, "dư", "p1", "x", [], [])); }, "Tối đa 50 nháp");
    await service.DeleteDraftAsync(new(draft.DraftId));
    Check((await service.ListAsync()).Drafts!.Count == 49, "Xóa nháp");
    var missingDelete = false;
    try { await service.DeleteDraftAsync(new(draft.DraftId)); } catch (KeyNotFoundException) { missingDelete = true; }
    Check(missingDelete, "Xóa nháp không tồn tại báo lỗi");

    Console.WriteLine($"Facebook application smoke: {passed} PASS; SQLite thật, browser giả, live NOT_RUN");
    return 0;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

sealed class FakePersonalPoster : IFacebookPoster
{
    public int Calls { get; private set; }
    public bool Unknown { get; set; }
    public async Task<FacebookPostResult> PostAsync(FacebookPostInput input, Func<Task> beforeSubmit, CancellationToken ct)
    {
        Calls++;
        await beforeSubmit();
        if (Unknown) throw new FacebookUnknownOutcomeException("Không thấy kết quả");
        return new(FacebookPostOutcome.Published, null, "published");
    }
}
sealed class FakeBrowser : IFacebookGroupBrowser
{
    public FacebookGroupDiscovery Discovery { get; set; } = new([], true, "Đủ");
    public Func<FacebookGroupPostInput, Func<Task>, Task<FacebookPostResult>>? Post { get; set; }
    public int Calls { get; set; }
    public Exception? DiscoverError { get; set; }
    public Task<FacebookGroupDiscovery> DiscoverGroupsAsync(string profileId, string accountIdentity, CancellationToken ct) => DiscoverError != null ? Task.FromException<FacebookGroupDiscovery>(DiscoverError) : Task.FromResult(Discovery);
    public async Task<FacebookPostResult> PostGroupAsync(FacebookGroupPostInput input, Func<Task> beforeSubmit, CancellationToken ct)
    {
        Calls++;
        if (Post != null) return await Post(input, beforeSubmit);
        await beforeSubmit(); return new(FacebookPostOutcome.Published, null, "published");
    }
}
sealed class FakeController : IBrowserProcessController
{
    public event EventHandler<string>? ContextClosed { add { } remove { } }
    public bool IsRunning(string profileId) => false;
    public IBrowserContextHandle? TryGetContext(string profileId) => null;
    public Task<IBrowserContextHandle> LaunchPersistentContextAsync(string profileId, bool isHeadless, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IBrowserContextHandle> LaunchPersistentContextAsync(string profileId, bool isHeadless, string? initialUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task NavigateAsync(string profileId, string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CloseContextAsync(string profileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PrewarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class TestClock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Current { get; set; } = now; public override DateTimeOffset GetUtcNow() => Current; }
