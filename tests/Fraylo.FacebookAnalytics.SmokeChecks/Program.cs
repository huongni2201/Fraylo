using System.Text.Json;
using Fraylo.Application.Analytics;
using Fraylo.Application.FacebookAnalytics;
using Fraylo.Application.Jobs;
using Fraylo.Application.Providers;
using Fraylo.Application.Settings;
using Fraylo.Browser;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Analytics;
using Fraylo.Browser.Publishing;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.FacebookAnalytics;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;
using Fraylo.Desktop.Bridge;
using Fraylo.Desktop.Bridge.Handlers;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;
using Fraylo.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

var passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
async Task Reject(Func<Task> run, string name) { try { await run(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
var root = Path.Combine(Directory.GetCurrentDirectory(), ".tmp", "fba-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "test.db");
await new SqliteDatabaseBootstrapper().BootstrapAsync(dbPath);
var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite(DatabasePathHelper.BuildConnectionString(dbPath)).AddInterceptors(new SqlitePragmaInterceptor()).Options;
var registry = new ProfileRegistry(() => new FrayloDbContext(options), Path.Combine(root, "profiles"));
var fake = new FakeInsights();
var controller = new FakeController();
var providerRegistry = new ProviderRegistry();
providerRegistry.RegisterAdapter(new AnalyticsFixtureAdapter());
await using var analysisScheduler = new JobScheduler(options, new JobReservation(options), new ProductionJobExecutor(providerRegistry));
var analysisJobs = new JobService(options, new JobSnapshotFactory(new SettingsService(options, new StorageRootRegistry(options))), analysisScheduler);
var analysis = new AnalyticsAnalysisService(options, providerRegistry, analysisJobs, analysisScheduler);
await using var service = new FacebookAnalyticsService(options, registry, controller, fake, analysis);
await using (var db = new FrayloDbContext(options))
{
    foreach (var id in new[] { "a", "b" })
    {
        db.BrowserProfiles.Add(new() { Id = id, Kind = "Facebook", ProviderKey = "facebook", AccountIdentity = "Facebook (1)", SessionStatus = "Connected", DisplayName = id });
        db.ProviderAccounts.Add(new() { Id = "account-" + id, ProfileId = id, ProviderKey = "facebook", Enabled = true });
    }
    db.AppSettings.Add(new() { Key = SettingsService.KeyAnalyticsProvider, Value = ProviderKeys.ChatGPTWeb });
    await db.SaveChangesAsync();
}
var metric = FacebookInsightsBrowser.ParseMetric;
Check(metric("‑‑").Value == null && metric("0").Value == 0, "Missing khác số 0");
Check(metric("1.234").Value == 1234 && metric("1,2K").Value == 1200 && metric("1,2K").Approximate, "Số VI và xấp xỉ giữ raw/precision");
Check(metric("4\n0 từ quảng cáo").Value == 4 && metric("1.2.3").Value == null, "Không cộng paid, từ chối số sai định dạng");
Check(FacebookInsightsBrowser.ParseSeconds("3 giây").Value == 3 && FacebookInsightsBrowser.ParseSeconds("13.492.803").Value == null, "Không đoán đơn vị thời gian");
var catalog = await service.CatalogAsync();
Check(catalog.Accounts.Count == 2 && fake.Calls == 0, "Đọc catalog chỉ dùng SQLite");
catalog = await service.SyncPagesAsync(new("a"));
await service.SyncPagesAsync(new("b"));
var binding = catalog.Pages.Single();
var query = new FacebookAnalyticsRequest("a", binding.Id);
await Reject(() => service.DashboardAsync(query with { AccountId = "b" }), "Không đọc binding thuộc tài khoản khác");
await Reject(() => service.StartAsync(query with { OperationId = "bad" }), "Operation ID hợp lệ bắt buộc");
var started = await service.StartAsync(query with { OperationId = Guid.NewGuid().ToString() });
async Task<FacebookAnalyticsDashboard> WaitDone(FacebookAnalyticsRequest request)
{
    for (var i = 0; i < 100; i++) { var value = await service.DashboardAsync(request); if (value.LatestAttempt?.Status != "Running") return value; await Task.Delay(30); }
    throw new Exception("Sync never finished");
}
var done = await WaitDone(query);
Check(done.Capture?.Status == "Partial" && done.Totals["views"].Value == null && done.Totals["comments"].Value == 0, "Lưu null/0 và độ đầy đủ theo capture");
Check(done.Capture?.ActivityLog?.Select(e => e.Code).SequenceEqual(["checking_session", "reading_insights", "Partial"]) == true,
    "Nhật ký Analytics được lưu cùng kết quả");
var readCalls = fake.Calls;
var replay = await service.StartAsync(query with { OperationId = started.Id });
Check(replay.Id == started.Id && fake.Calls == readCalls, "Replay cùng operation không mở browser lại");
fake.Read = (_, _, _, _, ct) => Task.FromResult(new FacebookContentRead([FakeInsights.Content(40)], true, "ok"));
await Task.Delay(5);
await service.StartAsync(query with { OperationId = Guid.NewGuid().ToString() });
done = await WaitDone(query);
Check(done.Totals["views"].Value == 40 && done.Content.Count == 1, "Tổng dùng đúng capture mới, không cộng lịch sử");
var observations = await service.HistoryAsync(query with { ContentId = "123" });
Check(observations.Count == 2 && observations[0].Content.Metrics["views"].Value == 40, "Hai mốc lịch sử cùng nội dung được giữ");
await Reject(() => service.HistoryAsync(query with { AccountId = "b", ContentId = "123" }), "History tách account/Page");
Check((await service.DashboardAsync(query with { Search = "không khớp" })).TotalCount == 0, "Search áp lên đúng tập capture");
await Reject(() => service.DashboardAsync(query with { Sort = "unsafe" }), "Bộ lọc sort được allow-list");
fake.Read = (_, _, _, _, _) => throw new InvalidOperationException("no insights");
await service.StartAsync(query with { OperationId = Guid.NewGuid().ToString() });
done = await WaitDone(query);
Check(done.LatestAttempt?.Status == "Failed" && done.Capture?.Status == "Complete" && done.Totals["views"].Value == 40, "Lỗi mới không xóa/ghi đè dữ liệu thành công cũ");
Check(done.LatestAttempt?.ActivityLog?.Last().ReasonCode == "insights_unavailable", "Lượt Analytics thất bại giữ mã lỗi");
fake.Read = async (_, _, _, _, ct) => { await Task.Delay(30000, ct); return new([], false, "unused"); };
var cancel = await service.StartAsync(query with { OperationId = Guid.NewGuid().ToString() });
await Reject(() => service.StartAsync(query with { OperationId = Guid.NewGuid().ToString() }), "Bấm lặp không chạy hai sync cùng profile");
await Reject(() => service.CancelAsync(query with { AccountId = "b", OperationId = cancel.Id }), "Cancel sai scope bị chặn");
await service.CancelAsync(query with { OperationId = cancel.Id });
done = await WaitDone(query);
Check(done.LatestAttempt?.Status == "Canceled" && done.Totals["views"].Value == 40, "Hủy không mất dữ liệu cũ");
await using (var db = new FrayloDbContext(options))
{
    db.FacebookAnalyticsCaptures.Add(new() { Id = Guid.NewGuid().ToString("N"), PageId = binding.Id, StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"), Owner = "0:0" });
    await db.SaveChangesAsync();
}
await service.RecoverInterruptedAsync();
Check((await service.DashboardAsync(query)).LatestAttempt?.Status == "Failed", "Crash recovery không tự chạy browser");
await using (var db = new FrayloDbContext(options))
{
    var reports = await db.Set<AnalyticsAnalysisReport>().Include(r => r.Job).OrderBy(r => r.CreatedAtUtc).ToListAsync();
    Check(reports.Count == 2 && reports.All(r => r.Platform == "facebook" && r.AccountId == "a" && r.PageId == binding.Id &&
        r.Job?.TaskType == JobTaskType.AnalyticsAnalysis), "AI tạo đúng một job cho mỗi capture Complete/Partial, không gửi lại Failed/Canceled/replay");
    using var input = JsonDocument.Parse(reports[0].InputJson!);
    var row = input.RootElement.GetProperty("sample")[0];
    Check(input.RootElement.GetProperty("partial").GetBoolean() && input.RootElement.GetProperty("scope").GetString()!.Contains("Trọn đời") &&
        row.GetProperty("metrics").GetProperty("views").GetProperty("value").ValueKind == JsonValueKind.Null &&
        row.GetProperty("metrics").GetProperty("comments").GetProperty("value").GetInt32() == 0,
        "AI snapshot Facebook giữ Partial, lifetime, null và số 0.");
}
await using (var db = new FrayloDbContext(options))
{
    await db.ProviderAccounts.Where(a => a.ProfileId == "a").ExecuteUpdateAsync(s => s.SetProperty(a => a.Enabled, false));
}
await Reject(() => service.StartAsync(query with { OperationId = Guid.NewGuid().ToString() }), "Tài khoản tắt không mở browser để cập nhật");
Check((await service.DashboardAsync(query)).Content.Count == 1, "Tài khoản tắt vẫn xem số liệu cục bộ");
await using (var db = new FrayloDbContext(options))
{
    await db.ProviderAccounts.Where(a => a.ProfileId == "a").ExecuteUpdateAsync(s => s.SetProperty(a => a.Enabled, true));
    db.AppSettings.Add(new() { Key = "scheduler.max_open_browsers", Value = "1" });
    await db.BrowserProfiles.Where(p => p.Id == "b").ExecuteUpdateAsync(s => s.SetProperty(p => p.OperationalState, "Running"));
    await db.SaveChangesAsync();
}
await Reject(() => service.StartAsync(query with { OperationId = Guid.NewGuid().ToString() }), "Analytics tuân thủ ngân sách browser toàn app");
var dispatcher = new BridgeDispatcher();
var handler = new FacebookAnalyticsHandlers(service); handler.Register(dispatcher);
Check(FacebookAnalyticsHandlers.Commands.All(dispatcher.IsCommandAllowed), "Bridge đăng ký allow-list riêng");
var envelope = new RequestEnvelope(Guid.NewGuid(), "request", "facebook.analytics.dashboard", JsonSerializer.SerializeToElement(new { accountId = "b", pageId = binding.Id }), DateTimeOffset.UtcNow, "1.1.0");
Check(!(await handler.HandleAsync(envelope, default)).Success, "Bridge từ chối request sai account");
var catalogResponse = await handler.HandleAsync(envelope with { Command = "facebook.analytics.catalog" }, default);
Check(catalogResponse.Data?.TryGetProperty("accounts", out _) == true, "DTO JSON camelCase đồng bộ React");
// Lỗi lưu kết quả không được giữ slot Running vô hạn hoặc tự gọi Facebook lần nữa.
await using (var db = new FrayloDbContext(options))
{
    await db.AppSettings.Where(s => s.Key == "scheduler.max_open_browsers").ExecuteDeleteAsync();
    await db.BrowserProfiles.Where(p => p.Id == "b").ExecuteUpdateAsync(s => s.SetProperty(p => p.OperationalState, "Stopped"));
    await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_capture_save BEFORE UPDATE ON FacebookAnalyticsCapture WHEN NEW.status IN ('Complete','Partial') BEGIN SELECT RAISE(ABORT, 'fixture save failure'); END;");
}
fake.Read = (_, _, _, _, _) => Task.FromResult(new FacebookContentRead([FakeInsights.Content(99)], true, "ok"));
var callsBeforeSaveFailure = fake.Calls;
var saveFailure = await service.StartAsync(query with { OperationId = Guid.NewGuid().ToString() });
var recoveredSaveFailure = false;
for (var i = 0; i < 100; i++)
{
    await service.DashboardAsync(query);
    await using var db = new FrayloDbContext(options);
    var capture = await db.FacebookAnalyticsCaptures.SingleAsync(c => c.Id == saveFailure.Id);
    if (capture.Status == "Failed") { recoveredSaveFailure = capture.Message!.StartsWith("save_failed:"); break; }
    await Task.Delay(30);
}
Check(recoveredSaveFailure && fake.Calls == callsBeforeSaveFailure + 1, "Lỗi lưu được phục hồi Failed ngay trong phiên, không gọi lại browser");
Check((await service.StartAsync(query with { OperationId = saveFailure.Id })).Status == "Failed", "Replay lượt lưu lỗi không tự chạy lại");
Check((await service.DashboardAsync(query)).Totals["views"].Value == 40, "Lỗi lưu giữ nguyên capture thành công trước đó");
await using (var db = new FrayloDbContext(options)) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_capture_save;");
var afterSaveFailure = await service.StartAsync(query with { OperationId = Guid.NewGuid().ToString() });
for (var i = 0; i < 100; i++)
{
    await using var db = new FrayloDbContext(options);
    if ((await db.FacebookAnalyticsCaptures.SingleAsync(c => c.Id == afterSaveFailure.Id)).Status == "Complete") break;
    await Task.Delay(30);
}
await using (var db = new FrayloDbContext(options))
    Check((await db.FacebookAnalyticsCaptures.SingleAsync(c => c.Id == afterSaveFailure.Id)).Status == "Complete", "Có thể cập nhật lại sau khi nơi lưu hoạt động trở lại");

if (args.Contains("--browser"))
{
    using var pw = await Playwright.CreateAsync();
    await using var engine = await pw.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true });
    var context = await engine.NewContextAsync();
    await context.RouteAsync("**/*", route => route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Fixtures.Content.Replace("\\u200b", "\u200b") }));
    var page = await context.NewPageAsync(); await page.GotoAsync("https://business.facebook.com/latest/posts/published_posts?asset_id=100");
    var result = await FacebookInsightsBrowser.ReadContentOnPageAsync(page, "Page test", _ => { }, default);
    Check(result.Content.Single().ContentId == "123" && result.Content.Single().Metrics["reactions"].Value == 4 && result.Content.Single().PublishedDate == "2024-05-23", "Browser đọc ID/column theo nhãn, bỏ zero-width và giữ date");
    await page.GetByRole(AriaRole.Columnheader, new() { Name = "Bình luận" }).EvaluateAsync("e=>e.textContent='Changed'");
    await Reject(() => FacebookInsightsBrowser.ReadContentOnPageAsync(page, "Page test", _ => { }, default), "UI thay đổi dừng thay vì lấy sai cột");
    await page.SetContentAsync("""
        <main><section><a href="https://web.facebook.com/profile.php?id=555">Page test</a><div><a href="https://web.facebook.com/latest/inbox/all?asset_id=100">Tin nhắn</a><a href="https://web.facebook.com/ad_center/create/ad/?page_id=100">Quảng cáo</a></div></section></main>
        """);
    var pages = await FacebookInsightsBrowser.ReadPagesOnPageAsync(page);
    Check(pages.Single().FacebookPageId == "100" && pages.Single().Name == "Page test", "Page discovery lấy asset_id cùng card, không dùng public profile ID");
    using (FacebookGroupBrowser.AcquireProfileOperation("profile-test"))
        await Reject(() => new FacebookInsightsBrowser(controller).DiscoverAsync("profile-test", "Facebook (1)", default), "Analytics dùng chung ownership với đăng/login Facebook");
    await context.CloseAsync();
}
Console.WriteLine($"Facebook Analytics: {passed} PASS (SQLite thật; live Facebook không thuộc fixture).");

sealed class AnalyticsFixtureAdapter : IProviderAdapter
{
    public string ProviderKey => ProviderKeys.ChatGPTWeb;
    public IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities() =>
        [new(ProviderKey, JobTaskType.AnalyticsAnalysis, ProviderVerificationStatus.Trial, true, false, false, 8000, null, null, 1)];
    public Task<ProviderTaskCapabilityDto?> GetCapabilityForTaskAsync(string taskType, CancellationToken ct = default) =>
        Task.FromResult(GetCapabilities().SingleOrDefault(c => c.TaskType == taskType));
    public Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, Func<Task> before,
        Func<int, string, Task>? progress, CancellationToken ct = default) =>
        throw new InvalidOperationException("Fixture chỉ xác minh bước tự enqueue.");
}

sealed class FakeInsights : IFacebookInsightsBrowser
{
    public int Calls;
    public Func<string, string, string, string, CancellationToken, Task<FacebookContentRead>>? Read;
    public static FacebookContentDto Content(double? views = null) => new("123", "Reel", "Test Reel", "https://www.facebook.com/reel/123/", "23 Tháng 5, 2024", "2024-05-23",
        new Dictionary<string, FacebookMetric> { ["views"] = new(views, views?.ToString() ?? "‑‑"), ["reactions"] = new(4, "4"), ["comments"] = new(0, "0"), ["shares"] = new(0, "0") });
    public Task<IReadOnlyList<FacebookDiscoveredPage>> DiscoverAsync(string profileId, string identity, CancellationToken ct) { Calls++; return Task.FromResult<IReadOnlyList<FacebookDiscoveredPage>>([new("100", "Page test", "https://www.facebook.com/page-test")]); }
    public Task<FacebookContentRead> ReadAsync(string profileId, string identity, string pageId, string pageName, Action<string> progress, CancellationToken ct)
    { Calls++; progress("Đang đọc số liệu"); return Read?.Invoke(profileId, identity, pageId, pageName, ct) ?? Task.FromResult(new FacebookContentRead([Content()], false, "partial")); }
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

static class Fixtures
{
    public const string Content = """
<div role="grid" aria-label="Bài viết đã đăng"><div role="row"><div role="columnheader">Chọn tất cả</div><div role="columnheader">Tiêu đề</div><div role="columnheader">Ngày đăng</div><div role="columnheader">Lượt xem​</div><div role="columnheader">Lượt thích và cảm xúc</div><div role="columnheader">Bình luận</div><div role="columnheader">Lượt chia sẻ</div><div role="columnheader">Thời gian phát video trung bình</div></div>
<div role="row"><div role="gridcell"><input type="checkbox" aria-label="Chọn mục có ID 123"></div><div role="gridcell">Caption test<br>Thước phim<br>Page test</div><div role="gridcell">23 Tháng 5, 2024</div><div role="gridcell">‑‑</div><div role="gridcell">4<br>0 từ quảng cáo</div><div role="gridcell">0</div><div role="gridcell">0</div><div role="gridcell">3 giây</div></div></div>
""";

}
