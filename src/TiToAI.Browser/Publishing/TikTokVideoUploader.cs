using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TiToAI.Browser.Abstractions;
using TiToAI.Browser.Models;
using TiToAI.Contracts.Accounts;

namespace TiToAI.Browser.Publishing;

public sealed record TikTokUploadInput(
    string ProfileId, string AccountIdentity, string FilePath, string Caption, string Privacy);

public interface ITikTokVideoUploader
{
    Task<string?> PublishAsync(TikTokUploadInput input, Func<Task> beforeSubmit, CancellationToken cancellationToken);
}

/// <summary>
/// Hợp đồng DOM nghiêm ngặt: dừng khi thiếu/không duy nhất, không tự đoán nút hoặc bỏ CAPTCHA.
/// shortcut: hợp đồng EN/VI được kiểm thử bằng fixture; cần qualification trên TikTok thật trước phát hành.
/// </summary>
public sealed class TikTokVideoUploader(
    IBrowserProcessController controller, IProfileIdentityResolver identityResolver) : ITikTokVideoUploader
{
    public const string UploadUrl = "https://www.tiktok.com/tiktokstudio/upload";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProfileOperations = new(StringComparer.OrdinalIgnoreCase);

    public static bool ShouldBlockResource(string method, string resourceType, string url)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return false;
        if (resourceType is "image" or "font") return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Host is "www.google-analytics.com" or "www.googletagmanager.com";
    }

    public static string NormalizeIdentity(string identity)
    {
        var value = identity.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host is not ("www.tiktok.com" or "tiktok.com")) return string.Empty;
            value = uri.AbsolutePath.Trim('/');
        }
        return value.TrimStart('@').ToLowerInvariant();
    }

    public async Task<string?> PublishAsync(
        TikTokUploadInput input, Func<Task> beforeSubmit, CancellationToken cancellationToken)
    {
        var operation = ProfileOperations.GetOrAdd(input.ProfileId, _ => new SemaphoreSlim(1, 1));
        if (!operation.Wait(0)) throw new InvalidOperationException("Tài khoản TikTok đang đăng video khác; hãy chờ hoàn tất.");
        bool ownsContext = false;
        IPage? page = null;
        try
        {
            var handle = controller.TryGetContext(input.ProfileId);
            if (handle == null)
            {
                handle = await controller.LaunchPersistentContextAsync(input.ProfileId, true, cancellationToken);
                ownsContext = true;
            }
            if (!handle.IsHeadless)
                throw new InvalidOperationException("Hãy hoàn tất đăng nhập và đóng cửa sổ TikTok trong Cài đặt rồi thử lại.");
            var identity = await identityResolver.ResolveProfileIdentityAsync(input.ProfileId, "tiktok", cancellationToken);
            if (!identity.IsSuccess || identity.SessionStatus != BrowserSessionStatus.Connected)
                throw new InvalidOperationException("Cần đăng nhập TikTok thủ công trong Cài đặt trước khi đăng video.");
            var expected = NormalizeIdentity(input.AccountIdentity);
            if (expected.Length == 0 || expected != NormalizeIdentity(identity.AccountIdentity ?? ""))
                throw new InvalidOperationException("Danh tính trong trình duyệt khác tài khoản TikTok đã chọn; chưa tải video lên.");

            page = await handle.Context.NewPageAsync();
            await page.RouteAsync("**/*", route => ShouldBlockResource(
                route.Request.Method, route.Request.ResourceType, route.Request.Url)
                    ? route.AbortAsync() : route.FallbackAsync());
            var response = await page.GotoAsync(UploadUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            EnsureUploadResponse(response);
            EnsureTikTokUrl(page.Url);
            await UploadAndFillAsync(page, input, cancellationToken);
            var post = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Post|Đăng)$", RegexOptions.IgnoreCase) });
            await RequireUniqueAsync(post, "nút Đăng");
            await Microsoft.Playwright.Assertions.Expect(post).ToBeEnabledAsync(new() { Timeout = 120000 });
            await EnsureNoChallengeAsync(page);
            await VerifyContentAsync(page, input);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureTikTokUrl(page.Url);
            // Journal phải commit trước click. Mọi lỗi kể từ đây đều cần đối soát, không retry tự động.
            await beforeSubmit();
            cancellationToken.ThrowIfCancellationRequested();
            await post.ClickAsync(new() { Timeout = 5000 });

            var success = page.GetByRole(AriaRole.Alert).Or(page.GetByRole(AriaRole.Status)).Filter(new()
            {
                HasTextRegex = new Regex("Your video has been posted|Video (published|posted)|Đã đăng video|Video đã được đăng", RegexOptions.IgnoreCase)
            });
            await success.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
            var links = await success.Locator("a[href]").EvaluateAllAsync<string[]>("els => els.map(e => e.href)");
            return links.FirstOrDefault(url => IsPostUrlForIdentity(url, expected));
        }
        finally
        {
            try
            {
                if (ownsContext) await controller.CloseContextAsync(input.ProfileId, CancellationToken.None);
                else if (page != null && !page.IsClosed) await page.CloseAsync();
            }
            finally { operation.Release(); }
        }
    }

    public static bool IsPostUrlForIdentity(string url, string identity)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host is not ("www.tiktok.com" or "tiktok.com")) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 3 && NormalizeIdentity(parts[0]) == NormalizeIdentity(identity) &&
            parts[1] == "video" && parts[2].Length > 0 && parts[2].All(char.IsAsciiDigit);
    }

    public static async Task UploadAndFillAsync(IPage page, TikTokUploadInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureNoChallengeAsync(page);
        var file = page.Locator("input[type=file]");
        await RequireUniqueAsync(file, "ô nhận video", attached: true);
        await file.SetInputFilesAsync(input.FilePath, new() { Timeout = 30000 });

        var caption = page.Locator("[contenteditable=true][role=textbox]");
        if (await caption.CountAsync() == 0) caption = page.Locator("[contenteditable=true]");
        await RequireUniqueAsync(caption, "ô caption");
        await caption.FocusAsync();
        await caption.PressAsync("ControlOrMeta+A");
        await page.Keyboard.InsertTextAsync(input.Caption);
        var actual = await caption.InnerTextAsync();
        if (NormalizeCaption(actual) != NormalizeCaption(input.Caption))
            throw new InvalidOperationException("TikTok không giữ nguyên caption/hashtag. Đã dừng trước khi đăng.");

        var privacy = page.GetByRole(AriaRole.Combobox, new()
        {
            NameRegex = new Regex("Who can (watch|view)|Ai có thể (xem|xem video)", RegexOptions.IgnoreCase)
        });
        await RequireUniqueAsync(privacy, "quyền xem video");
        var label = PrivacyLabel(input.Privacy);
        bool nativeSelect = await privacy.EvaluateAsync<bool>("el => el.tagName === 'SELECT'");
        if (nativeSelect)
        {
            var option = privacy.GetByRole(AriaRole.Option, new() { NameRegex = label });
            if (await option.CountAsync() != 1) throw new InvalidOperationException("Không tìm thấy quyền xem đã chọn.");
            await privacy.SelectOptionAsync(new SelectOptionValue { Label = (await option.InnerTextAsync()).Trim() });
        }
        else
        {
            await privacy.ClickAsync();
            var option = page.GetByRole(AriaRole.Option, new() { NameRegex = label });
            await RequireUniqueAsync(option, "lựa chọn quyền xem");
            await option.ClickAsync();
        }
        var selected = nativeSelect
            ? await privacy.EvaluateAsync<string>("el => el.selectedOptions[0].textContent.trim()")
            : (await privacy.InnerTextAsync()).Trim();
        if (!label.IsMatch(selected)) throw new InvalidOperationException("Không xác nhận được quyền xem video; đã dừng trước khi đăng.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static Regex PrivacyLabel(string privacy) => new(privacy switch
    {
        "private" => "^(Only (you|me)|Chỉ (mình )?tôi|Chỉ bạn)$",
        "public" => "^(Everyone|Mọi người)$",
        "friends" => "^(Friends|Bạn bè)$",
        _ => throw new ArgumentException("Quyền xem không hợp lệ.")
    }, RegexOptions.IgnoreCase);

    private static async Task VerifyContentAsync(IPage page, TikTokUploadInput input)
    {
        var caption = page.Locator("[contenteditable=true][role=textbox]");
        if (await caption.CountAsync() == 0) caption = page.Locator("[contenteditable=true]");
        await RequireUniqueAsync(caption, "ô caption trước khi đăng");
        if (NormalizeCaption(await caption.InnerTextAsync()) != NormalizeCaption(input.Caption))
            throw new InvalidOperationException("Caption đã thay đổi trong lúc xử lý video; chưa đăng.");
        var privacy = page.GetByRole(AriaRole.Combobox, new()
        { NameRegex = new Regex("Who can (watch|view)|Ai có thể (xem|xem video)", RegexOptions.IgnoreCase) });
        await RequireUniqueAsync(privacy, "quyền xem trước khi đăng");
        var selected = await privacy.EvaluateAsync<string>("el => (el.tagName === 'SELECT' ? el.selectedOptions[0].textContent : el.textContent).trim()");
        if (!PrivacyLabel(input.Privacy).IsMatch(selected))
            throw new InvalidOperationException("Quyền xem đã thay đổi trong lúc xử lý video; chưa đăng.");
    }

    private static string NormalizeCaption(string text) => Regex.Replace(text.Replace('\u00a0', ' '), @"\s+", " ").Trim();

    private static async Task RequireUniqueAsync(ILocator locator, string name, bool attached = false)
    {
        await locator.First.WaitForAsync(new() { State = attached ? WaitForSelectorState.Attached : WaitForSelectorState.Visible, Timeout = 30000 });
        if (await locator.CountAsync() != 1) throw new InvalidOperationException($"Giao diện TikTok không khớp: {name} không duy nhất.");
    }

    private static void EnsureTikTokUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "www.tiktok.com" || !uri.AbsolutePath.StartsWith("/tiktokstudio/", StringComparison.Ordinal))
            throw new InvalidOperationException("TikTok yêu cầu đăng nhập/xác minh hoặc đã chuyển trang. Hoàn tất trong Cài đặt.");
    }

    // shortcut: danh sách cảnh báo theo chữ EN/VI, chỉ kiểm qua fixture; bổ sung khi gặp cảnh báo thật.
    private static readonly Regex WarningText = new(
        "unusual activity|hoạt động bất thường|too many (requests|attempts)|quá nhiều (yêu cầu|lần thử)|access denied|truy cập bị từ chối|temporarily (blocked|restricted|suspended)|tạm thời bị (chặn|hạn chế|đình chỉ)|verify to continue|xác minh để tiếp tục",
        RegexOptions.IgnoreCase);

    private static void EnsureUploadResponse(IResponse? response)
    {
        if (response is { Ok: false })
            throw new InvalidOperationException($"TikTok trả HTTP {response.Status} khi mở trang tải lên. Đã dừng, không thử lại tự động; kiểm tra trong Cài đặt.");
    }

    private static async Task EnsureNoChallengeAsync(IPage page)
    {
        var challenge = page.Locator("iframe[src*='captcha'], [id*='captcha'], [class*='captcha']");
        for (int i = 0; i < await challenge.CountAsync(); i++)
            if (await challenge.Nth(i).IsVisibleAsync())
                throw new InvalidOperationException("TikTok yêu cầu xác minh CAPTCHA. Hãy xác minh thủ công trong Cài đặt.");
        var visibleText = await page.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''");
        if (WarningText.IsMatch(visibleText))
            throw new InvalidOperationException("TikTok hiển thị cảnh báo hoặc xác minh; đã dừng trước khi đăng. Xử lý thủ công trong Cài đặt.");
    }
}
