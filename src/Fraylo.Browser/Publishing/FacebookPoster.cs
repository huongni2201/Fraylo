using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Probers;

namespace Fraylo.Browser.Publishing;

public enum FacebookPostOutcome { Published, PendingApproval }

/// <param name="ImagePaths">Tối đa một lô ảnh; bài chữ để rỗng. Không trộn ảnh và video (video chưa hỗ trợ).</param>
/// <param name="RequiredAudience">Nếu có, composer phải đang ở đúng đối tượng xem (vd "Only me"), sai thì dừng trước khi đăng.</param>
public sealed record FacebookPostInput(
    string ProfileId, string AccountIdentity, string Text, IReadOnlyList<string> ImagePaths, string? RequiredAudience = null, string? ExpectedActorUrl = null,
    Func<string, string, Task>? ReportStage = null);

public sealed record FacebookPostResult(FacebookPostOutcome Outcome, string? PostUrl, string? Detail);

/// <summary>Dừng trước khi đăng vì cần người dùng: session_expired | checkpoint.</summary>
public sealed class FacebookNeedsUserException(string reasonCode, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>Lỗi chắc chắn chưa đăng (trước click) hoặc bị Facebook từ chối rõ ràng (rejected_by_facebook).</summary>
public sealed class FacebookPostException(string reasonCode, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>Đã bấm Đăng nhưng không có bằng chứng nào: không được tự gửi lại.</summary>
public sealed class FacebookUnknownOutcomeException(string message) : Exception(message);

public interface IFacebookPoster
{
    Task<FacebookPostResult> PostAsync(FacebookPostInput input, Func<Task> beforeSubmit, CancellationToken cancellationToken);
}

/// <summary>
/// Đăng bài lên trang cá nhân (Giai đoạn A/D của FACEBOOK_PUBLISHING_PLAN). Hợp đồng DOM EN theo FACEBOOK_UI_EVIDENCE E1–E12;
/// shortcut: Page và video chưa hỗ trợ; selector không duy nhất thì dừng. Selector EN/VI dùng cùng hợp đồng composer; VI cần qualification trang cá nhân thật.
/// </summary>
public sealed class FacebookPoster(IBrowserProcessController controller) : IFacebookPoster
{
    internal static readonly ConcurrentDictionary<string, SemaphoreSlim> ProfileOperations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex ComposerEntry = new("What's on your mind|Bạn đang nghĩ gì", RegexOptions.IgnoreCase);
    private static readonly Regex PublishedText = new(@"^Your post has been published[.!]?$", RegexOptions.IgnoreCase);
    private static readonly Regex PendingText = new(@"^Your post (is pending approval by admins|has been submitted for admin approval)[.!]?$", RegexOptions.IgnoreCase);
    private static readonly Regex RejectedText = new("couldn't|can't post|something went wrong|try again|isn't available", RegexOptions.IgnoreCase);

    public async Task<FacebookPostResult> PostAsync(FacebookPostInput input, Func<Task> beforeSubmit, CancellationToken ct)
    {
        var operation = ProfileOperations.GetOrAdd(input.ProfileId, _ => new SemaphoreSlim(1, 1));
        if (!operation.Wait(0)) throw new InvalidOperationException("Tài khoản Facebook đang đăng bài khác; hãy chờ hoàn tất.");
        var ownsContext = false;
        IPage? page = null;
        try
        {
            var handle = controller.TryGetContext(input.ProfileId);
            if (handle == null)
            {
                handle = await controller.LaunchPersistentContextAsync(input.ProfileId, isHeadless: true, cancellationToken: ct);
                ownsContext = true;
            }
            if (!ownsContext && !handle.IsHeadless)
                throw new InvalidOperationException("Hãy hoàn tất đăng nhập và đóng cửa sổ Facebook trong Cài đặt rồi thử lại.");

            if (input.ReportStage != null) await input.ReportStage("checking_target", "Đang xác minh tài khoản và trang cá nhân cần đăng.");
            page = await handle.Context.NewPageAsync();
            await page.GotoAsync(ProviderUrlResolver.DefaultFacebookUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            await page.WaitForTimeoutAsync(3000);
            EnsureNoChallenge(page);

            var cookies = await handle.Context.CookiesAsync(new[] { ProviderUrlResolver.DefaultFacebookUrl });
            var identity = FacebookPlatformProber.ReadIdentity(cookies.Select(c => (c.Name, c.Value)));
            if (identity == null) throw new FacebookNeedsUserException("session_expired", "Phiên Facebook hết hạn; hãy đăng nhập lại trong Cài đặt.");
            if (identity != input.AccountIdentity)
                throw new FacebookPostException("identity_mismatch", "Danh tính trong trình duyệt khác tài khoản Facebook đã chọn; chưa đăng.");

            var accountId = Regex.Match(input.AccountIdentity, @"^Facebook [(]([0-9]+)[)]$");
            if (!accountId.Success) throw new FacebookPostException("identity_mismatch", "Danh tính Facebook không hợp lệ; chưa đăng.");
            await page.GotoAsync("https://www.facebook.com/profile.php?id=" + accountId.Groups[1].Value, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            await page.WaitForTimeoutAsync(1500);
            EnsureNoChallenge(page);
            var actorUrl = page.Url;
            if (!Uri.TryCreate(actorUrl, UriKind.Absolute, out var actor) || actor.Host != "www.facebook.com" || actor.AbsolutePath.TrimEnd('/') == "/me")
                throw new FacebookPostException("identity_mismatch", "Không xác minh được URL trang cá nhân; chưa đăng.");
            await page.GotoAsync(ProviderUrlResolver.DefaultFacebookUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            await page.WaitForTimeoutAsync(1500);
            return await PostOnPageAsync(page, input with { ExpectedActorUrl = actorUrl }, beforeSubmit, TimeSpan.FromSeconds(60), ct);
        }
        finally
        {
            if (page is { IsClosed: false }) { try { await page.CloseAsync(); } catch { } }
            if (ownsContext) { try { await controller.CloseContextAsync(input.ProfileId, CancellationToken.None); } catch { } }
            operation.Release();
        }
    }

    /// <summary>Thao tác trên trang chủ Facebook đã đăng nhập. Tách riêng để kiểm thử bằng fixture.</summary>
    public static async Task<FacebookPostResult> PostOnPageAsync(
        IPage page, FacebookPostInput input, Func<Task> beforeSubmit, TimeSpan resultTimeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Text) && input.ImagePaths.Count == 0)
            throw new ArgumentException("Bài đăng cần có chữ hoặc ảnh.");
        var submitted = false;
        try
        {
            EnsureNoChallenge(page);
            await DismissRememberPasswordAsync(page);

            if (input.ReportStage != null) await input.ReportStage("opening_composer", "Đang mở khung soạn bài trên trang cá nhân.");
            var entry = page.GetByRole(AriaRole.Button).Filter(new() { HasTextRegex = ComposerEntry });
            if (await entry.CountAsync() != 1) throw new FacebookPostException("composer_not_found", "Không tìm thấy duy nhất một nút tạo bài viết.");
            await entry.ClickAsync(new() { Timeout = 10000 });

            // Dialog ngoài cùng (dialog "Create post" lồng một dialog cùng tên làm tiêu đề).
            var dialog = page.GetByRole(AriaRole.Dialog).First;
            var textbox = dialog.GetByRole(AriaRole.Textbox);
            await textbox.First.WaitForAsync(new() { Timeout = 20000 });
            if (await textbox.CountAsync() != 1) throw new FacebookPostException("composer_not_found", "Composer có nhiều ô nhập; dừng trước khi đăng.");

            if (input.ExpectedActorUrl != null)
            {
                var actors = dialog.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("(^Dòng thời gian của |'s Timeline$)", RegexOptions.IgnoreCase) });
                if (await actors.CountAsync() != 1 || ActorKey(await actors.GetAttributeAsync("href") ?? "") != ActorKey(input.ExpectedActorUrl))
                    throw new FacebookPostException("identity_mismatch", "Danh tính trong composer khác trang cá nhân đã chọn; chưa đăng.");
            }
            var audienceButton = dialog.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Edit privacy", RegexOptions.IgnoreCase) });
            if (input.RequiredAudience is { Length: > 0 } wanted)
            {
                var ok = await dialog.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Edit privacy.*" + Regex.Escape(wanted), RegexOptions.IgnoreCase) }).CountAsync() == 1;
                if (!ok) throw new FacebookPostException("audience_mismatch", $"Đối tượng xem không phải '{wanted}'; chưa đăng.");
            }

            // Evidence E9: đính media trước, gõ chữ sau.
            if (input.ImagePaths.Count > 0)
            {
                if (input.ReportStage != null) await input.ReportStage("uploading_media", "Đang đính kèm ảnh vào bài đăng.");
                await AttachImagesAsync(page, dialog, input.ImagePaths);
            }
            if (input.ReportStage != null) await input.ReportStage("checking_composer", "Đang điền và kiểm tra nội dung trước khi gửi.");
            if (!string.IsNullOrEmpty(input.Text))
            {
                await textbox.ClickAsync();
                await page.Keyboard.InsertTextAsync(input.Text);
                await page.WaitForTimeoutAsync(500);
            }
            if (Normalize(await textbox.InnerTextAsync()) != Normalize(input.Text))
                throw new FacebookPostException("content_mismatch", "Nội dung trong composer không khớp nội dung cần đăng; chưa đăng.");
            var attached = await FacebookGroupBrowser.AttachedMediaCountAsync(dialog);
            if (attached != input.ImagePaths.Count)
                throw new FacebookPostException("media_mismatch", $"Số ảnh đã đính ({attached}) khác dự kiến ({input.ImagePaths.Count}); chưa đăng.");

            var post = dialog.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Post|Đăng)$") });
            await Assertions.Expect(post).ToBeEnabledAsync(new() { Timeout = 30000 });
            EnsureNoChallenge(page);
            ct.ThrowIfCancellationRequested();

            var previousMessages = (await page.GetByRole(AriaRole.Alert).Or(page.GetByRole(AriaRole.Status)).AllInnerTextsAsync()).Select(t => t.Trim()).ToHashSet(StringComparer.Ordinal);
            await beforeSubmit();
            submitted = true;
            ct.ThrowIfCancellationRequested();
            await post.ClickAsync(new() { Timeout = 5000 });
            return await WaitForResultAsync(page, resultTimeout, previousMessages);
        }
        finally
        {
            if (!submitted) await DiscardDraftAsync(page);
        }
    }

    private static async Task<FacebookPostResult> WaitForResultAsync(IPage page, TimeSpan timeout, IReadOnlySet<string> previousMessages)
    {
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var alerts = page.GetByRole(AriaRole.Alert).Or(page.GetByRole(AriaRole.Status));
                var count = await alerts.CountAsync();
                for (var i = 0; i < count; i++)
                {
                    if (!await alerts.Nth(i).IsVisibleAsync()) continue;
                    var text = (await alerts.Nth(i).InnerTextAsync()).Trim();
                    if (previousMessages.Contains(text)) continue;
                    if (PublishedText.IsMatch(text)) return new(FacebookPostOutcome.Published, null, text);
                    if (PendingText.IsMatch(text)) return new(FacebookPostOutcome.PendingApproval, null, text);
                    if (RejectedText.IsMatch(text)) throw new FacebookPostException("rejected_by_facebook", text);
                }
                await page.WaitForTimeoutAsync(500);
            }
        }
        catch (PlaywrightException ex)
        {
            throw new FacebookUnknownOutcomeException($"Mất kết nối sau khi bấm Đăng: {ex.Message}");
        }
        throw new FacebookUnknownOutcomeException("Đã bấm Đăng nhưng không thấy xác nhận từ Facebook; kiểm tra thủ công, không tự gửi lại.");
    }

    private static async Task AttachImagesAsync(IPage page, ILocator dialog, IReadOnlyList<string> paths)
    {
        await dialog.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^(Photo\/video|Ảnh\/video)$") }).ClickAsync();
        var inputs = page.Locator("input[type=file]");
        if (await inputs.CountAsync() > 1) throw new FacebookPostException("media_rejected", "Không xác định duy nhất nơi nhận ảnh; chưa đăng.");
        await inputs.First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 });
        await inputs.First.SetInputFilesAsync(paths);
        var until = DateTime.UtcNow.AddSeconds(60);
        try
        {
            while (await FacebookGroupBrowser.AttachedMediaCountAsync(dialog) != paths.Count)
            {
                if (DateTime.UtcNow >= until) throw new FacebookPostException("media_rejected", "Facebook không nhận đủ ảnh; chưa đăng.");
                await page.WaitForTimeoutAsync(500);
            }
        }
        catch (PlaywrightException) { throw new FacebookPostException("media_rejected", "Facebook không nhận đủ ảnh đính kèm; chưa đăng."); }
    }

    private static async Task DismissRememberPasswordAsync(IPage page)
    {
        var heading = page.GetByRole(AriaRole.Heading, new() { Name = "Remember Password" });
        if (await heading.CountAsync() != 1) return;
        var notNow = page.GetByRole(AriaRole.Button, new() { Name = "Not Now", Exact = true });
        if (await notNow.CountAsync() == 1) { await notNow.ClickAsync(); await page.WaitForTimeoutAsync(500); }
    }

    private static void EnsureNoChallenge(IPage page)
    {
        if (FacebookPlatformProber.IsLoginOrCheckpointUrl(page.Url))
            throw new FacebookNeedsUserException(page.Url.Contains("/checkpoint", StringComparison.OrdinalIgnoreCase) ? "checkpoint" : "session_expired",
                "Facebook yêu cầu đăng nhập hoặc xác minh; hãy xử lý thủ công trong Cài đặt.");
    }

    /// <summary>Hủy composer chưa đăng: Escape rồi "Delete draft" (E10). Không bao giờ chọn "Save as draft".</summary>
    private static async Task DiscardDraftAsync(IPage page)
    {
        try
        {
            if (page.IsClosed || await page.GetByRole(AriaRole.Dialog).CountAsync() == 0) return;
            await page.Keyboard.PressAsync("Escape");
            var delete = page.GetByRole(AriaRole.Button, new() { Name = "Delete draft", Exact = true });
            try { await delete.WaitForAsync(new() { Timeout = 3000 }); await delete.ClickAsync(); } catch (TimeoutException) { }
        }
        catch (PlaywrightException) { }
    }

    private static string ActorKey(string url)
    {
        if (!Uri.TryCreate(new Uri("https://www.facebook.com/"), url, out var uri) || uri.Scheme != "https" || uri.Host != "www.facebook.com") return "invalid:" + url;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path != "/profile.php") return path;
        var id = System.Text.RegularExpressions.Regex.Match(uri.Query, @"(?:[?&])id=([0-9]+)(?:&|$)");
        return id.Success ? path + "?id=" + id.Groups[1].Value : "invalid:" + url;
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Trim();
}
