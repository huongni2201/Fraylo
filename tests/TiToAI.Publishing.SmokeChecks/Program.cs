using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using TiToAI.Application.Publishing;
using TiToAI.Browser;
using TiToAI.Browser.Models;
using TiToAI.Browser.Publishing;
using TiToAI.Contracts.Bridge;
using TiToAI.Contracts.Accounts;
using TiToAI.Contracts.Publishing;
using TiToAI.Desktop.Bridge;
using TiToAI.Desktop.Bridge.Handlers;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;
using TiToAI.Infrastructure.Storage;

namespace TiToAI.Publishing.SmokeChecks;

public static class Program
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

    public static async Task<int> Main()
    {
        var temp = Path.Combine(Path.GetTempPath(), "titoai-publishing-" + Guid.NewGuid().ToString("N"));
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
            Pass("URL đối soát phải thuộc TikTok và đúng tài khoản.");

            var options = new DbContextOptionsBuilder<TiToAIDbContext>().UseSqlite($"Data Source={Path.Combine(temp, "test.db")};Pooling=False").Options;
            await using (var db = new TiToAIDbContext(options)) await db.Database.EnsureCreatedAsync();
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
            await using (var db = new TiToAIDbContext(options))
            {
                db.Projects.Add(new() { Id = projectId, Name = "Video thử", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.Assets.Add(new() { Id = assetId, ProjectId = projectId, AssetType = "Video", MimeType = "video/mp4", StorageRootId = StorageRootRegistry.RootDefaultVideosId, RelativePath = "output.mp4", Sha256Hash = hash, ByteSize = 12, CreatedAtUtc = now });
                db.Scenes.Add(new() { Id = sceneId, ProjectId = projectId, SequenceOrder = 1, Name = "Video", VideoAssetId = assetId, Status = "Completed", CreatedAtUtc = now, UpdatedAtUtc = now });
                await db.SaveChangesAsync();
            }
            var fake = new FakeUploader();
            var service = new TikTokPublishingService(options, roots, profiles, fake);
            PublishVideoRequest NewRequest() => new(Guid.NewGuid().ToString("N"), projectId, assetId, accountId, "Caption tiếng Việt #thoitrang 🎬", "private");
            Check((await service.ListAsync()).Videos.Count == 1, "Video cuối xuất hiện");
            Pass("Danh sách chỉ lấy video được liên kết vào đầu ra Project.");
            await Reject(() => service.PublishAsync(NewRequest() with { AssetId = Guid.NewGuid().ToString("D") }));
            await Reject(() => service.PublishAsync(NewRequest() with { AccountId = Guid.NewGuid().ToString("D") }));
            await Reject(() => service.PublishAsync(NewRequest() with { Caption = new string('x', 2201) }));
            await Reject(() => service.PublishAsync(NewRequest() with { Privacy = "invalid" }));
            Check(fake.Calls == 0, "Input không hợp lệ không gọi browser");
            Pass("Chặn video ngoài Project, account không tồn tại, caption quá dài và privacy sai.");

            await using (var db = new TiToAIDbContext(options))
            {
                db.AppSettings.Add(new() { Key = "scene.workflow." + sceneId, Value = "{\"isStale\":true}", UpdatedAtUtc = now });
                await db.SaveChangesAsync();
            }
            Check((await service.ListAsync()).Videos.Count == 0, "Không đưa video stale vào danh sách");
            await Reject(() => service.PublishAsync(NewRequest()));
            await using (var db = new TiToAIDbContext(options))
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
            Check(uncertain.State == "Uncertain", "Timeout sau submit phải uncertain");
            var calls = fake.Calls;
            Check((await service.PublishAsync(uncertainRequest)).AttemptId == uncertain.AttemptId && fake.Calls == calls, "Replay không gửi lại");
            await Reject(() => service.PublishAsync(NewRequest()));
            await Reject(() => service.PublishAsync(uncertainRequest with { Caption = "Nội dung khác" }));
            Pass("Timeout sau submit + replay + request mới không gây đăng trùng.");

            await Reject(() => service.ReconcileAsync(new(uncertain.AttemptId, true, "https://www.tiktok.com/@other/video/12")));
            var checkedNotPosted = await service.ReconcileAsync(new(uncertain.AttemptId, false));
            Check(checkedNotPosted.State == "UserConfirmedNotPublished", "Không đánh dấu Published");
            fake.Action = async (before, _) =>
            {
                await before();
                await using var db = new TiToAIDbContext(options);
                var raw = await db.AppSettings.SingleAsync(s => s.Key.StartsWith("publishing.attempt.") && s.Value.Contains("Submitted"));
                Check(raw.Value.Contains("Submitted"), "Checkpoint đã commit trước side effect");
                return "https://www.tiktok.com/@creator/video/12345";
            };
            var publishedRequest = NewRequest();
            var published = await service.PublishAsync(publishedRequest);
            Check(published.State == "Published" && published.ExternalPostUrl != null, "Xác nhận thực của adapter");
            await Reject(() => service.PublishAsync(NewRequest()));
            Pass("Đối soát chưa đăng mở lại thao tác; Published giữ khóa chống trùng.");

            // Giả lập crash sau checkpoint: cùng journal bền vững, service mới phải giữ Uncertain.
            await using (var db = new TiToAIDbContext(options))
            {
                var row = await db.AppSettings.SingleAsync(s => s.Key == "publishing.attempt." + published.AttemptId);
                var json = JsonNode.Parse(row.Value)!;
                json["attempt"]!["state"] = "Submitted";
                row.Value = json.ToJsonString();
                await db.SaveChangesAsync();
            }
            service = new(options, roots, profiles, fake);
            var recovered = (await service.ListAsync()).Attempts.Single(a => a.AttemptId == published.AttemptId);
            Check(recovered.State == "Uncertain", "Khôi phục trạng thái uncertain");
            await Reject(() => service.PublishAsync(NewRequest()));
            await service.ReconcileAsync(new(published.AttemptId, false));
            Pass("Restart giữ lịch sử và khóa uncertain, không resend.");

            var revisionId = Guid.NewGuid().ToString("D");
            await using (var db = new TiToAIDbContext(options))
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
                await using var db = new TiToAIDbContext(options);
                db.AppSettings.Add(new() { Key = "scene.workflow." + sceneId, Value = "{\"isStale\":true}", UpdatedAtUtc = now });
                await db.SaveChangesAsync();
                await before();
                throw new Exception("Không được click Post sau khi output đổi");
            };
            Check((await service.PublishAsync(NewRequest())).State == "Failed", "Output stale trong lúc upload phải dừng trước submit");
            await using (var db = new TiToAIDbContext(options))
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
            await Reject(() => service.ReconcileAsync(new(concurrentRequest.ClientRequestId, false)));
            release.SetResult();
            await inFlight;
            Pass("Tác vụ đồng thời và đối soát khi đang chạy đều bị khóa.");

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

            await DomFixtureAsync(videoPath, profiles, profileId, temp);
            Console.WriteLine($"Kết quả: {_passed} nhóm PASS; live TikTok NOT_RUN.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        // Giữ DB/profile fixture trong Temp để kiểm tra khi fail; không chạm dữ liệu TiToAI thật.
    }

    private static async Task DomFixtureAsync(string videoPath, ProfileRegistry profiles, string profileId, string temp)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "msedge" });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("""
            <input type="file" accept="video/*" hidden>
            <div role="textbox" contenteditable="true">Old caption</div>
            <label>Who can watch this video <select aria-label="Who can watch this video">
            <option value="public">Everyone</option><option value="private">Only you</option><option value="friends">Friends</option>
            </select></label><button>Post</button>
            """);
        var input = new TikTokUploadInput("fixture", "@creator", videoPath, "Caption tiếng Việt #hashtag 🎬", "private");
        await TikTokVideoUploader.UploadAndFillAsync(page, input, CancellationToken.None);
        Check(await page.Locator("input").EvaluateAsync<int>("e => e.files.length") == 1, "File input ẩn đã nhận file");
        Check((await page.GetByRole(AriaRole.Textbox).InnerTextAsync()) == input.Caption, "Giữ caption Unicode/hashtag");
        Check(await page.GetByRole(AriaRole.Combobox).InputValueAsync() == "private", "Privacy đã đổi đúng");
        Pass("Playwright thật với fixture: hidden file input + InsertText Unicode + xác minh privacy.");
        await page.EvaluateAsync("() => { document.querySelector('[contenteditable]').addEventListener('input', e => e.target.textContent = 'lost'); }");
        await Reject(() => TikTokVideoUploader.UploadAndFillAsync(page, input, CancellationToken.None));
        await page.SetContentAsync("<div id='captcha'>Verify</div><input type='file' hidden>");
        await Reject(() => TikTokVideoUploader.UploadAndFillAsync(page, input, CancellationToken.None));
        Check(await page.Locator("input").EvaluateAsync<int>("e => e.files.length") == 0, "CAPTCHA chặn upload");
        await page.SetContentAsync("<div>Unusual activity detected</div><input type='file' hidden>");
        await Reject(() => TikTokVideoUploader.UploadAndFillAsync(page, input, CancellationToken.None));
        Check(await page.Locator("input").EvaluateAsync<int>("e => e.files.length") == 0, "Cảnh báo chữ chặn upload");
        Pass("DOM mất caption/CAPTCHA/cảnh báo phải dừng trước khi đăng, không bypass.");

        // Toàn bộ request được fulfill bằng HTML cục bộ; không liên hệ TikTok thật.
        using var owner = new ProfileOwner(id => Path.Combine(temp, "controller", id));
        int status = 200;
        await using var controller = new PlaywrightBrowserController(profiles, owner, customPersistentLauncher: async (_, _) =>
        {
            var context = await browser.NewContextAsync();
            await context.RouteAsync("**/*", route => route.FulfillAsync(new()
            {
                Status = status,
                ContentType = "text/html", Body = """
                <input type="file" hidden><div role="textbox" contenteditable="true"></div>
                <select aria-label="Who can watch this video"><option>Everyone</option><option>Only you</option></select>
                <button onclick="document.getElementById('result').innerHTML='Video posted <a href=https://www.tiktok.com/@creator/video/12345>View</a>'">Post</button>
                <div id="result" role="status"></div>
                """
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
        Pass("Toàn adapter đi qua upload/caption/privacy/checkpoint/Post/success; giữ context của caller.");
        status = 403;
        bool blockedCheckpoint = false;
        await Reject(() => uploader.PublishAsync(input, () => { blockedCheckpoint = true; return Task.CompletedTask; }, CancellationToken.None));
        status = 200;
        Check(!blockedCheckpoint, "HTTP 403 phải dừng trước checkpoint");
        Pass("HTTP 403 khi mở trang tải lên dừng trước checkpoint, không gửi lại.");
        await Reject(() => uploader.PublishAsync(input with { AccountIdentity = "@other" }, () => throw new Exception("Không được submit sai account"), CancellationToken.None));
        Check(!preservedPage.IsClosed, "Sai account không đóng context của caller");
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = uploader.PublishAsync(input, async () => { submitted.SetResult(); await continuePost.Task; }, CancellationToken.None);
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Reject(() => uploader.PublishAsync(input, () => Task.CompletedTask, CancellationToken.None));
        continuePost.SetResult(); await pending;
        Pass("Sai danh tính hoặc đăng đồng thời cùng profile đều bị chặn.");
        await controller.CloseContextAsync(profileId);
        var visible = await controller.LaunchPersistentContextAsync(profileId, false);
        await Reject(() => uploader.PublishAsync(input, () => throw new Exception("Không được submit khi login đang mở"), CancellationToken.None));
        Check(controller.IsRunning(profileId) && !visible.Context.Pages.Single().IsClosed, "Không chiếm/đóng cửa sổ login");
        Pass("Giữ cửa sổ login của người dùng; không chạy upload trong context visible.");
    }

    private sealed class FixtureIdentityResolver : IProfileIdentityResolver
    {
        public Task<ProfileIdentityResolutionResult> ResolveProfileIdentityAsync(string profileId, string provider, CancellationToken ct = default)
            => Task.FromResult(ProfileIdentityResolutionResult.Success(profileId, provider, "@creator", "Connected"));
    }

    private sealed class FakeUploader : ITikTokVideoUploader
    {
        public int Calls { get; private set; }
        public Func<Func<Task>, CancellationToken, Task<string?>> Action { get; set; } = (_, _) => throw new InvalidOperationException();
        public Task<string?> PublishAsync(TikTokUploadInput input, Func<Task> beforeSubmit, CancellationToken ct)
        { Calls++; return Action(beforeSubmit, ct); }
    }
}
