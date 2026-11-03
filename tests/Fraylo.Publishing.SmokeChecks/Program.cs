using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Fraylo.Application.Publishing;
using Fraylo.Browser;
using Fraylo.Browser.Models;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Publishing;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Accounts;
using Fraylo.Contracts.Publishing;
using Fraylo.Desktop.Bridge;
using Fraylo.Desktop.Bridge.Handlers;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Publishing.SmokeChecks;

public static partial class Program
{
    private static int _passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Pass(string message) { _passed++; Console.WriteLine($"PASS {_passed}: {message}"); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { return; }
        throw new InvalidOperationException("Phải từ chối thao tác không hợp lệ.");
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--favorites-dom") || args.Contains("--upload-notices"))
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "msedge" });
            if (args.Contains("--upload-notices")) await UploadNoticesDomFixtureAsync(await browser.NewPageAsync());
            else await FavoriteSoundsDomFixtureAsync(await browser.NewPageAsync());
            return 0;
        }
        var temp = Path.Combine(Path.GetTempPath(), "Fraylo-publishing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Check(TikTokVideoUploader.ShouldBlockResource("GET", "image", "https://tiktok.com/a.png"), "Chặn ảnh");
            Check(TikTokVideoUploader.ShouldBlockResource("GET", "font", "https://tiktok.com/a.woff"), "Chặn font");
            Check(!TikTokVideoUploader.ShouldBlockResource("POST", "image", "https://tiktok.com/upload"), "Giữ mọi request upload");
            Check(!TikTokVideoUploader.ShouldBlockResource("GET", "media", "https://tiktok.com/preview"), "Giữ media xử lý");
            Check(!TikTokVideoUploader.ShouldBlockResource("POST", "fetch", "https://www.google-analytics.com/test"), "Không chặn POST");
            Check(!TikTokVideoUploader.ShouldBlockResource("GET", "script", "https://tiktok.com/app.js"), "Giữ JS lõi");
            Pass("Route chỉ chặn tài nguyên GET không cần thiết, giữ upload/preview/JS.");
            Check(TikTokVideoUploader.IsPostUrlForIdentity("https://www.tiktok.com/@creator/video/12345", "@creator"), "URL đúng");
            Check(!TikTokVideoUploader.IsPostUrlForIdentity("https://www.tiktok.com.evil.test/@creator/video/12345", "@creator"), "Host giả");
            Check(!TikTokVideoUploader.IsPostUrlForIdentity("https://www.tiktok.com/@other/video/12345", "@creator"), "Sai identity");
            Pass("URL bài đăng phải thuộc TikTok và đúng tài khoản.");

            var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite($"Data Source={Path.Combine(temp, "test.db")};Pooling=False").Options;
            await using (var db = new FrayloDbContext(options)) await db.Database.EnsureCreatedAsync();
            var roots = new StorageRootRegistry(options, temp);
            await roots.EnsureDefaultRootsAsync();
            var profiles = new ProfileRegistry(() => new(options), Path.Combine(temp, "profiles"));
            var profileId = Guid.NewGuid().ToString("N");
            await profiles.EnsureProfileAsync(profileId, BrowserProfileKind.TikTok, accountIdentity: "@creator");
            await profiles.UpdateProfileStateAsync(profileId, sessionStatus: BrowserSessionStatus.Connected);
            var projectId = Guid.NewGuid().ToString("D");
            var accountId = profileId;
            var assetId = Guid.NewGuid().ToString("D");
            var sceneId = Guid.NewGuid().ToString("D");
            var videoDir = Path.Combine(temp, "videos");
            Directory.CreateDirectory(videoDir);
            var videoPath = Path.Combine(videoDir, "output.mp4");
            await File.WriteAllBytesAsync(videoPath, [0, 0, 0, 24, 102, 116, 121, 112, 109, 112, 52, 50]);
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(videoPath))).ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using (var db = new FrayloDbContext(options))
            {
                db.Projects.Add(new() { Id = projectId, Name = "Video thử", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.Assets.Add(new() { Id = assetId, ProjectId = projectId, AssetType = "Video", MimeType = "video/mp4", StorageRootId = StorageRootRegistry.RootDefaultVideosId, RelativePath = "output.mp4", Sha256Hash = hash, ByteSize = 12, CreatedAtUtc = now });
                db.Scenes.Add(new() { Id = sceneId, ProjectId = projectId, SequenceOrder = 1, Name = "Video", VideoAssetId = assetId, Status = "Completed", CreatedAtUtc = now, UpdatedAtUtc = now });
                await db.SaveChangesAsync();
            }
            var fake = new FakeUploader();
            var service = new TikTokPublishingService(options, roots, profiles, fake);
            var favoritesRequest = new FavoriteSoundsRequest(projectId, assetId, accountId);
            var favorites = await service.GetFavoriteSoundsAsync(favoritesRequest);
            Check(favorites.AccountId == accountId && favorites.Sounds.Single().Title == "@creator", "Favorites đúng account");
            await Reject(() => service.GetFavoriteSoundsAsync(favoritesRequest with { AccountId = Guid.NewGuid().ToString("N") }));
            await Reject(() => service.GetFavoriteSoundsAsync(favoritesRequest with { AssetId = Guid.NewGuid().ToString("D") }));
            await profiles.UpdateProfileStateAsync(profileId, sessionStatus: BrowserSessionStatus.Unverified);
            await Reject(() => service.GetFavoriteSoundsAsync(favoritesRequest));
            await profiles.UpdateProfileStateAsync(profileId, sessionStatus: BrowserSessionStatus.Connected);
            Check(fake.FavoriteCalls == 1 && fake.Calls == 0, "Account/video/session sai không vào browser hoặc publish");
            Pass("Favorites kiểm scope video/account, session và không đăng bài.");
            PublishVideoRequest NewRequest() => new(Guid.NewGuid().ToString("N"), projectId, assetId, accountId, "Caption tiếng Việt #thoitrang 🎬");
            Check((await service.ListAsync()).Videos.Count == 1, "Video cuối xuất hiện");
            Check((await service.ListAsync()).Videos.Single().CreatedAtUtc == now, "Video có thời điểm tạo của asset");
            Pass("Danh sách chỉ lấy video được liên kết vào đầu ra Project.");
            await Reject(() => service.PublishAsync(NewRequest() with { AssetId = Guid.NewGuid().ToString("D") }));
            await Reject(() => service.PublishAsync(NewRequest() with { AccountId = Guid.NewGuid().ToString("D") }));
            await Reject(() => service.PublishAsync(NewRequest() with { Caption = new string('x', 2201) }));
            Check(fake.Calls == 0, "Input không hợp lệ không gọi browser");
            Pass("Chặn video ngoài Project, account không tồn tại và caption quá dài.");

            await using (var db = new FrayloDbContext(options))
            {
                db.AppSettings.Add(new() { Key = "scene.workflow." + sceneId, Value = "{\"isStale\":true}", UpdatedAtUtc = now });
                await db.SaveChangesAsync();
            }
            Check((await service.ListAsync()).Videos.Count == 0, "Không đưa video stale vào danh sách");
            await Reject(() => service.PublishAsync(NewRequest()));
            await using (var db = new FrayloDbContext(options))
            {
                db.AppSettings.Remove(await db.AppSettings.SingleAsync(s => s.Key == "scene.workflow." + sceneId));
                var extra = new Scene { Id = Guid.NewGuid().ToString("D"), ProjectId = projectId, Name = "Clip thứ hai", SequenceOrder = 2, CreatedAtUtc = now, UpdatedAtUtc = now };
                db.Scenes.Add(extra); await db.SaveChangesAsync();
                Check((await service.ListAsync()).Videos.Count == 0, "Không xem nhiều clip là một video cuối");
                await Reject(() => service.PublishAsync(NewRequest()));
                db.Scenes.Remove(extra); await db.SaveChangesAsync();
            }
            Pass("Video stale hoặc Project nhiều clip không đủ điều kiện đăng.");

            fake.Action = (_, _) => throw new InvalidOperationException("Login required");
            var failed = await service.PublishAsync(NewRequest());
            Check(failed.State == "Failed", "Lỗi trước submit phải cho thử lại");
            Pass("Lỗi trước submit không khóa video vĩnh viễn.");
            fake.Action = async (before, _) => { await before(); throw new TimeoutException("Connection lost"); };
            var uncertainRequest = NewRequest();
            var uncertain = await service.PublishAsync(uncertainRequest);
            Check(uncertain.State == "Failed" && uncertain.Message.Contains("Connection lost"), "Timeout sau submit phải Failed và giữ lỗi gốc");
            var calls = fake.Calls;
            Check((await service.PublishAsync(uncertainRequest)).AttemptId == uncertain.AttemptId && fake.Calls == calls, "Replay không gửi lại");
            await Reject(() => service.PublishAsync(uncertainRequest with { Caption = "Nội dung khác" }));
            await Reject(() => service.PublishAsync(uncertainRequest with { FavoriteSoundKey = new string('a', 64) }));
            Pass("Timeout sau submit giữ lỗi gốc; replay không gửi lại, yêu cầu mới được phép đăng.");

            fake.Action = async (before, _) =>
            {
                await before();
                await using var db = new FrayloDbContext(options);
                var raw = await db.AppSettings.SingleAsync(s => s.Key.StartsWith("publishing.attempt.") && s.Value.Contains("Submitted"));
                Check(raw.Value.Contains("Submitted"), "Checkpoint đã commit trước side effect");
                return "https://www.tiktok.com/@creator/video/12345";
            };
            var publishedRequest = NewRequest();
            var published = await service.PublishAsync(publishedRequest);
            Check(published.State == "Published" && published.ExternalPostUrl != null, "Xác nhận thực của adapter");
            await Reject(() => service.PublishAsync(NewRequest()));
            Pass("Sau Failed được đăng lại thủ công; Published giữ khóa chống trùng.");

            // Giả lập crash sau checkpoint: khôi phục Failed, không tự gửi lại.
            await using (var db = new FrayloDbContext(options))
            {
                var row = await db.AppSettings.SingleAsync(s => s.Key == "publishing.attempt." + published.AttemptId);
                var json = JsonNode.Parse(row.Value)!;
                json["attempt"]!["state"] = "Submitted";
                row.Value = json.ToJsonString();
                await db.SaveChangesAsync();
            }
            service = new(options, roots, profiles, fake);
            var recovered = (await service.ListAsync()).Attempts.Single(a => a.AttemptId == published.AttemptId);
            Check(recovered.State == "Failed", "Khôi phục lần đăng gián đoạn thành Failed");
            Pass("Restart giữ lịch sử Failed, không resend.");
            await using (var db = new FrayloDbContext(options))
            {
                var row = await db.AppSettings.SingleAsync(s => s.Key == "publishing.attempt." + published.AttemptId);
                var json = JsonNode.Parse(row.Value)!;
                json["attempt"]!["state"] = "Uncertain";
                row.Value = json.ToJsonString();
                await db.SaveChangesAsync();
            }
            Check((await service.ListAsync()).Attempts.Single(a => a.AttemptId == published.AttemptId).State == "Failed",
                "Lịch sử Uncertain cũ phải chuyển Failed");
            Pass("Lịch sử cần đối soát cũ được chuyển Failed.");

            var revisionId = Guid.NewGuid().ToString("D");
            await using (var db = new FrayloDbContext(options))
            {
                db.VideoRevisions.Add(new() { Id = revisionId, ProjectId = projectId, Revision = 1, State = "Draft", AssetId = assetId, ContentHash = hash, CreatedAtUtc = now });
                await db.SaveChangesAsync();
                var project = await db.Projects.SingleAsync(p => p.Id == projectId);
                project.CurrentVideoRevisionId = revisionId; await db.SaveChangesAsync();
                Check((await service.ListAsync()).Videos.Count == 0, "Revision draft không sẵn sàng đăng");
                var revision = await db.VideoRevisions.SingleAsync(v => v.Id == revisionId);
                revision.State = "Ready"; await db.SaveChangesAsync();
                Check((await service.ListAsync()).Videos.Single().AssetId == assetId, "Chọn đúng CurrentVideoRevision Ready");
            }
            Pass("CurrentVideoRevision chỉ cho đăng khi Ready và đúng Project.");

            fake.Action = async (before, _) =>
            {
                await using var db = new FrayloDbContext(options);
                db.AppSettings.Add(new() { Key = "scene.workflow." + sceneId, Value = "{\"isStale\":true}", UpdatedAtUtc = now });
                await db.SaveChangesAsync();
                await before();
                throw new Exception("Không được click Post sau khi output đổi");
            };
            Check((await service.PublishAsync(NewRequest())).State == "Failed", "Output stale trong lúc upload phải dừng trước submit");
            await using (var db = new FrayloDbContext(options))
            {
                db.AppSettings.Remove(await db.AppSettings.SingleAsync(s => s.Key == "scene.workflow." + sceneId));
                await db.SaveChangesAsync();
            }
            Pass("Kiểm lại output ngay trước submit, không đăng bản đã đổi trong lúc upload.");

            await File.AppendAllTextAsync(videoPath, "changed");
            calls = fake.Calls;
            Check((await service.PublishAsync(NewRequest())).State == "Failed" && fake.Calls == calls, "Hash sai không gọi uploader");
            await File.WriteAllBytesAsync(videoPath, [0, 0, 0, 24, 102, 116, 121, 112, 109, 112, 52, 50]);
            Pass("File thay đổi bị chặn trước upload.");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.Action = async (before, _) => { await before(); entered.SetResult(); await release.Task; throw new TimeoutException(); };
            var concurrentRequest = NewRequest();
            var inFlight = service.PublishAsync(concurrentRequest);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var otherService = new TikTokPublishingService(options, roots, profiles, fake);
            Check((await otherService.ListAsync()).Attempts.Single(a => a.AttemptId == concurrentRequest.ClientRequestId).State == "Submitted", "Không recovery tác vụ đang chạy");
            await Reject(() => otherService.PublishAsync(NewRequest()));
            release.SetResult();
            await inFlight;
            Pass("Tác vụ đồng thời bị khóa khi đang chạy.");

            var dispatcher = new BridgeDispatcher();
            var handlers = new PublishingHandlers(service);
            handlers.Register(dispatcher);
            var envelope = new RequestEnvelope(Guid.NewGuid(), "request", "publishing.list", JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, BridgeContract.Version);
            var response = await dispatcher.DispatchAsync(envelope, CancellationToken.None);
            Check(response.Success && response.Data!.Value.TryGetProperty("videos", out _), "Bridge dùng camelCase");
            var invalid = await dispatcher.DispatchAsync(envelope with { Command = "publishing.publish", Payload = JsonSerializer.SerializeToElement(new { }) }, CancellationToken.None);
            Check(!invalid.Success, "Thiếu payload phải fail");
            handlers.Unregister(dispatcher);
            Check(!(await dispatcher.DispatchAsync(envelope, CancellationToken.None)).Success, "Unregister dọn handler");
            Pass("Bridge đăng ký/validation/camelCase/unregister hoạt động.");

            await Reject(() => service.PublishAsync(NewRequest() with { FavoriteSoundKey = "not-a-key" }));
            await Reject(() => service.PublishAsync(NewRequest() with { FavoriteSoundKey = new string('a', 64), SoundName = "ambiguous" }));
            await DomFixtureAsync(videoPath, profiles, profileId, temp);
            Console.WriteLine($"Kết quả: {_passed} nhóm PASS; live TikTok NOT_RUN.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        // Giữ DB/profile fixture trong Temp để kiểm tra khi fail; không chạm dữ liệu Fraylo thật.
    }

    private static async Task DomFixtureAsync(string videoPath, ProfileRegistry profiles, string profileId, string temp)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "msedge" });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("""
            <input type="file" accept="video/*" hidden>
            <div role="textbox" contenteditable="true">Old caption</div>
            <button>Post</button>
            """);
        var input = new TikTokUploadInput("fixture", "@creator", videoPath, "Caption tiếng Việt #hashtag 🎬");
        await TikTokVideoUploader.UploadAndFillAsync(page, input, CancellationToken.None);
        Check(await page.Locator("input").EvaluateAsync<int>("e => e.files.length") == 1, "File input ẩn đã nhận file");
        Check((await page.GetByRole(AriaRole.Textbox).InnerTextAsync()) == input.Caption, "Giữ caption Unicode/hashtag");
        Pass("Playwright thật với fixture: hidden file input + InsertText Unicode.");
        await page.EvaluateAsync("() => { document.querySelector('[contenteditable]').addEventListener('input', e => e.target.textContent = 'lost'); }");
        await Reject(() => TikTokVideoUploader.UploadAndFillAsync(page, input, CancellationToken.None));
        await page.SetContentAsync("<div id='captcha'>Verify</div><input type='file' hidden>");
        await Reject(() => TikTokVideoUploader.UploadAndFillAsync(page, input, CancellationToken.None));
        Check(await page.Locator("input").EvaluateAsync<int>("e => e.files.length") == 0, "CAPTCHA chặn upload");
        await page.SetContentAsync("<div>Unusual activity detected</div><input type='file' hidden>");
        await Reject(() => TikTokVideoUploader.UploadAndFillAsync(page, input, CancellationToken.None));
        Check(await page.Locator("input").EvaluateAsync<int>("e => e.files.length") == 0, "Cảnh báo chữ chặn upload");
        Pass("DOM mất caption/CAPTCHA/cảnh báo phải dừng trước khi đăng, không bypass.");

        const string soundFixture = """
            <div role="button" onclick="document.getElementById('editor').hidden=false"><svg></svg>Sounds</div>
            <div id="editor" hidden>
              <input placeholder="Search sounds" onkeydown="if(event.key==='Enter')document.getElementById('results').hidden=false">
              <ul id="results" hidden>
                <li>Other Song <button onclick="window.picked='other'"><svg></svg></button></li>
                <li>Trend Beat - Artist <button onclick="window.picked='trend'"><svg></svg></button></li>
              </ul>
              <button onclick="document.getElementById('editor').hidden=true">Cancel</button>
              <button onclick="document.getElementById('editor').hidden=true">Save</button>
            </div>
            """;
        await page.SetContentAsync(soundFixture);
        await TikTokVideoUploader.ApplySoundAsync(page, "trend beat", CancellationToken.None);
        Check(await page.EvaluateAsync<string>("() => window.picked") == "trend", "Chọn kết quả khớp tên, không phải kết quả đầu danh sách");
        Check(await page.Locator("#editor").IsHiddenAsync(), "Đã lưu và đóng trình chỉnh sửa");
        await page.SetContentAsync(soundFixture);
        await page.EvaluateAsync("() => { window.picked = null; }");
        await Reject(() => TikTokVideoUploader.ApplySoundAsync(page, "Không có bài này", CancellationToken.None));
        Check(await page.EvaluateAsync<string?>("() => window.picked ?? null") == null, "Không khớp thì không chọn sound nào");
        await page.SetContentAsync("<div>Chỉ có video, không có nút chỉnh sửa</div>");
        await Reject(() => TikTokVideoUploader.ApplySoundAsync(page, "Trend Beat", CancellationToken.None));
        Pass("Thêm sound: chọn đúng kết quả theo tên, lưu; không khớp hoặc thiếu nút thì dừng trước khi đăng.");

        await FavoriteSoundsDomFixtureAsync(page);

        // Toàn bộ request được fulfill bằng HTML cục bộ; không liên hệ TikTok thật.
        using var owner = new ProfileOwner(id => Path.Combine(temp, "controller", id));
        const string directPost = """
            <input type="file" hidden><div role="textbox" contenteditable="true"></div>
            <button onclick="document.getElementById('result').innerHTML='Video posted <a href=https://www.tiktok.com/@creator/video/12345>View</a>'">Post</button>
            <div id="result" role="status"></div>
            """;
        const string copyrightPrompt = """
            <input type="file" hidden><div role="textbox" contenteditable="true"></div>
            <button onclick="document.getElementById('dialog').hidden=false">Post</button>
            <div id="dialog" role="dialog" hidden>
              <p>Continue to post?</p>
              <button>Cancel</button>
              <button onclick="document.getElementById('dialog').hidden=true;document.getElementById('result').innerHTML='Video posted <a href=https://www.tiktok.com/@creator/video/12345>View</a>'">Post now</button>
            </div>
            <div id="result" role="status"></div>
            """;
        string markup = directPost;
        int status = 200;
        await using var controller = new PlaywrightBrowserController(profiles, owner, customPersistentLauncher: async (_, _) =>
        {
            var context = await browser.NewContextAsync();
            await context.RouteAsync("**/*", route => route.FulfillAsync(new()
            {
                Status = status,
                ContentType = "text/html; charset=utf-8", Body = markup
            }));
            await context.NewPageAsync();
            return context;
        });
        var handle = await controller.LaunchPersistentContextAsync(profileId, true);
        var preservedPage = handle.Context.Pages.Single();
        var uploader = new TikTokVideoUploader(controller, new FixtureIdentityResolver());
        input = input with { ProfileId = profileId };
        bool checkpoint = false;
        var url = await uploader.PublishAsync(input, () => { checkpoint = true; return Task.CompletedTask; }, CancellationToken.None);
        Check(checkpoint && url == "https://www.tiktok.com/@creator/video/12345", "Adapter phải submit một lần rồi đọc success URL");
        Check(controller.IsRunning(profileId) && !preservedPage.IsClosed && handle.Context.Pages.Count == 1,
            "Tái dùng context ẩn nhưng chỉ đóng page tự tạo");
        Pass("Toàn adapter đi qua upload/caption/checkpoint/Post/success; giữ context của caller.");
        markup = UploadNoticesFixture;
        bool noticeCheckpoint = false;
        await Reject(() => uploader.PublishAsync(input, () => { noticeCheckpoint = true; throw new InvalidOperationException("Dừng fixture trước Post"); }, CancellationToken.None));
        Check(noticeCheckpoint, "Đóng đúng hai notice để tìm thấy nút Post trước checkpoint");
        Pass("Đăng video: bỏ qua notice kiểm tra tự động và giới thiệu editor, tới checkpoint mà không tự bật tùy chọn.");
        markup = copyrightPrompt;
        var promptedUrl = await uploader.PublishAsync(input, () => Task.CompletedTask, CancellationToken.None);
        markup = directPost;
        Check(promptedUrl == url, "Popup Continue to post phải bấm Post now rồi đọc success URL");
        Pass("Popup Continue to post: bấm Post now trong cùng lần submit, vẫn đọc success URL.");
        var cleanupUploader = new TikTokVideoUploader(new FailingCloseController(handle), new FixtureIdentityResolver());
        var cleanupUrl = await cleanupUploader.PublishAsync(input, () => Task.CompletedTask, CancellationToken.None);
        Check(cleanupUrl == url, "Lỗi đóng context sau xác nhận không được đổi kết quả đã đăng");
        Pass("Đã đăng vẫn thành công khi cleanup browser lỗi.");
        status = 403;
        bool blockedCheckpoint = false;
        await Reject(() => uploader.PublishAsync(input, () => { blockedCheckpoint = true; return Task.CompletedTask; }, CancellationToken.None));
        status = 200;
        Check(!blockedCheckpoint, "HTTP 403 phải dừng trước checkpoint");
        Pass("HTTP 403 khi mở trang tải lên dừng trước checkpoint, không gửi lại.");
        await Reject(() => uploader.PublishAsync(input with { AccountIdentity = "@other" }, () => throw new Exception("Không được submit sai account"), CancellationToken.None));
        Check(!preservedPage.IsClosed, "Sai account không đóng context của caller");
        markup = FavoriteSoundsFixture;
        var pagesBeforeFavorites = handle.Context.Pages.Count;
        var favoriteResult = await uploader.GetFavoriteSoundsAsync(input, CancellationToken.None);
        Check(favoriteResult.Sounds.Count == 2 && !preservedPage.IsClosed && handle.Context.Pages.Count == pagesBeforeFavorites, $"Đọc Favorites, giữ context/page caller: {favoriteResult.Sounds.Count} bài, {handle.Context.Pages.Count}/{pagesBeforeFavorites} page");
        await Reject(() => uploader.GetFavoriteSoundsAsync(input with { AccountIdentity = "@other" }, CancellationToken.None));
        markup = directPost;
        Pass("Crawler Favorites thật trên fixture xác minh account, chỉ đóng page tự tạo và không bấm Post.");
        markup = directPost;
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = uploader.PublishAsync(input, async () => { submitted.SetResult(); await continuePost.Task; }, CancellationToken.None);
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Reject(() => uploader.PublishAsync(input, () => Task.CompletedTask, CancellationToken.None));
        await Reject(() => uploader.GetFavoriteSoundsAsync(input, CancellationToken.None));
        continuePost.SetResult(); await pending;
        Pass("Sai danh tính hoặc đăng đồng thời cùng profile đều bị chặn.");
        await controller.CloseContextAsync(profileId);
        var visible = await controller.LaunchPersistentContextAsync(profileId, false);
        await Reject(() => uploader.PublishAsync(input, () => throw new Exception("Không được submit khi login đang mở"), CancellationToken.None));
        await Reject(() => uploader.GetFavoriteSoundsAsync(input, CancellationToken.None));
        Check(controller.IsRunning(profileId) && !visible.Context.Pages.Single().IsClosed, "Không chiếm/đóng cửa sổ login");
        Pass("Giữ cửa sổ login của người dùng; không chạy upload trong context visible.");

        // Chrome thật (channel "chrome"), không custom launcher; trang cục bộ, không liên hệ TikTok.
        await controller.CloseContextAsync(profileId);
        using var realOwner = new ProfileOwner(id => Path.Combine(temp, "real", id));
        await using var realController = new PlaywrightBrowserController(profiles, realOwner);
        var real = await realController.LaunchPersistentContextAsync(profileId, true);
        var realPage = await real.Context.NewPageAsync();
        await realPage.SetContentAsync("<title>chrome-channel</title><p>ok</p>");
        var userAgent = await realPage.EvaluateAsync<string>("() => navigator.userAgent");
        Console.WriteLine($"UA Chrome thật: {userAgent}");
        Check(userAgent.Contains("Chrome/") && !userAgent.Contains("Chromium/"), "Launch channel chrome phải dùng Google Chrome");
        Pass("Controller khởi chạy Google Chrome thật qua channel chrome và tải trang cục bộ.");
    }

    private sealed class FailingCloseController(IBrowserContextHandle handle) : IBrowserProcessController
    {
        public event EventHandler<string>? ContextClosed { add { } remove { } }
        public IBrowserContextHandle? TryGetContext(string id) => null;
        public bool IsRunning(string id) => false;
        public Task<IBrowserContextHandle> LaunchVisibleAutomationContextAsync(string id, CancellationToken ct = default) => Task.FromResult(handle);
        public Task<IBrowserContextHandle> LaunchPersistentContextAsync(string id, bool headless, CancellationToken ct = default) => Task.FromResult(handle);
        public Task<IBrowserContextHandle> LaunchPersistentContextAsync(string id, bool headless, string? url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseContextAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("Fixture cleanup failed");
        public Task NavigateAsync(string id, string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PrewarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FixtureIdentityResolver : IProfileIdentityResolver
    {
        public Task<ProfileIdentityResolutionResult> ResolveProfileIdentityAsync(string profileId, string provider, CancellationToken ct = default)
            => Task.FromResult(ProfileIdentityResolutionResult.Success(profileId, provider, "@creator", "Connected"));
    }

    private sealed class FakeUploader : ITikTokVideoUploader
    {
        public int FavoriteCalls { get; private set; }
        public Task<FavoriteSoundsResult> GetFavoriteSoundsAsync(TikTokUploadInput input, CancellationToken ct)
        {
            FavoriteCalls++;
            return Task.FromResult(new FavoriteSoundsResult([new("fixture-key", input.AccountIdentity, "Artist", "00:21")], true));
        }
        public int Calls { get; private set; }
        public Func<Func<Task>, CancellationToken, Task<string?>> Action { get; set; } = (_, _) => throw new InvalidOperationException();
        public Task<string?> PublishAsync(TikTokUploadInput input, Func<Task> beforeSubmit, CancellationToken ct)
        { Calls++; return Action(beforeSubmit, ct); }
    }
}
