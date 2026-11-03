using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fraylo.Contracts.Publishing;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Accounts;

namespace Fraylo.Browser.Publishing;

public sealed record TikTokUploadInput(
    string ProfileId, string AccountIdentity, string FilePath, string Caption, string? SoundName = null, string? FavoriteSoundKey = null);

public sealed record FavoriteSoundsResult(IReadOnlyList<FavoriteSoundDto> Sounds, bool IsComplete);

public interface ITikTokVideoUploader
{
    Task<string?> PublishAsync(TikTokUploadInput input, Func<Task> beforeSubmit, CancellationToken cancellationToken);
    Task<FavoriteSoundsResult> GetFavoriteSoundsAsync(TikTokUploadInput input, CancellationToken cancellationToken);
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
        bool published = false;
        IPage? page = null;
        try
        {
            var handle = controller.TryGetContext(input.ProfileId);
            if (handle == null)
            {
                handle = await controller.LaunchPersistentContextAsync(input.ProfileId, isHeadless: true, cancellationToken: cancellationToken);
                ownsContext = true;
            }
            if (!ownsContext && !handle.IsHeadless)
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
            if (!string.IsNullOrWhiteSpace(input.FavoriteSoundKey))
                await ApplyFavoriteSoundAsync(page, input.FavoriteSoundKey, cancellationToken);
            else if (!string.IsNullOrWhiteSpace(input.SoundName))
                await ApplySoundAsync(page, input.SoundName, cancellationToken);
            var post = await WaitForUploadControlAsync(page, new Regex("^(Post|Đăng)$", RegexOptions.IgnoreCase), "nút Đăng", cancellationToken);
            await Microsoft.Playwright.Assertions.Expect(post).ToBeEnabledAsync(new() { Timeout = 120000 });
            await EnsureNoChallengeAsync(page);
            await VerifyContentAsync(page, input);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureTikTokUrl(page.Url);
            // Journal phải commit trước click; không tự gửi lại khi có lỗi.
            await beforeSubmit();
            cancellationToken.ThrowIfCancellationRequested();
            await post.ClickAsync(new() { Timeout = 5000 });
            await ConfirmPostNowAsync(page);

            var success = page.GetByRole(AriaRole.Alert).Or(page.GetByRole(AriaRole.Status)).Filter(new()
            {
                HasTextRegex = new Regex("Your video has been posted|Video (published|posted)|Đã đăng video|Video đã được đăng", RegexOptions.IgnoreCase)
            });
            await success.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
            published = true;
            var links = await success.Locator("a[href]").EvaluateAllAsync<string[]>("els => els.map(e => e.href)");
            return links.FirstOrDefault(url => IsPostUrlForIdentity(url, expected));
        }
        catch (PlaywrightException) when (published)
        {
            // URL là tùy chọn; trang đóng sau xác nhận không làm mất kết quả đã đăng.
            return null;
        }
        finally
        {
            try
            {
                if (ownsContext) await controller.CloseContextAsync(input.ProfileId, CancellationToken.None);
                else if (page != null && !page.IsClosed) await page.CloseAsync();
            }
            catch (Exception ex) when (published)
            {
                System.Diagnostics.Trace.TraceWarning("TikTok đã xác nhận đăng nhưng đóng browser lỗi: {0}", ex.Message);
            }
            finally { operation.Release(); }
        }
    }

    public async Task<FavoriteSoundsResult> GetFavoriteSoundsAsync(TikTokUploadInput input, CancellationToken cancellationToken)
    {
        var operation = ProfileOperations.GetOrAdd(input.ProfileId, _ => new SemaphoreSlim(1, 1));
        if (!operation.Wait(0)) throw new InvalidOperationException("Tài khoản đang đăng video hoặc tải nhạc; hãy chờ hoàn tất.");
        bool ownsContext = false;
        IPage? page = null;
        try
        {
            var handle = controller.TryGetContext(input.ProfileId);
            if (handle == null)
            {
                handle = await controller.LaunchPersistentContextAsync(input.ProfileId, isHeadless: true, cancellationToken: cancellationToken);
                ownsContext = true;
            }
            if (!ownsContext && !handle.IsHeadless)
                throw new InvalidOperationException("Hãy hoàn tất đăng nhập và đóng cửa sổ TikTok trong Cài đặt rồi tải nhạc.");
            var identity = await identityResolver.ResolveProfileIdentityAsync(input.ProfileId, "tiktok", cancellationToken);
            if (!identity.IsSuccess || identity.SessionStatus != BrowserSessionStatus.Connected ||
                NormalizeIdentity(input.AccountIdentity).Length == 0 ||
                NormalizeIdentity(input.AccountIdentity) != NormalizeIdentity(identity.AccountIdentity ?? ""))
                throw new InvalidOperationException("Phiên TikTok không khớp tài khoản đã chọn; hãy xác minh lại trong Cài đặt.");
            page = await handle.Context.NewPageAsync();
            var response = await page.GotoAsync(UploadUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            EnsureUploadResponse(response);
            EnsureTikTokUrl(page.Url);
            await UploadAndFillAsync(page, input with { Caption = "" }, cancellationToken);
            return await ReadFavoriteSoundsAsync(page, cancellationToken);
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

    // Đối chiếu AX TikTok Studio thật 2026-11-03: tab Yêu thích, panel-favorites, listitem tên + mm:ss · tác giả.
    // shortcut: key dựa trên metadata vì chưa xác minh music ID trong DOM; dùng ID khi đã đối chiếu trên TikTok thật.
    public static FavoriteSoundDto ParseFavoriteSound(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        var match = Regex.Match(normalized, @"^(?<title>.+?)(?<duration>\d{1,3}:[0-5]\d)\s*·\s*(?<author>.+)$");
        if (!match.Success || normalized.Length > 2000)
            throw new InvalidOperationException("Không đọc được thông tin nhạc yêu thích; giao diện TikTok có thể đã thay đổi.");
        var title = match.Groups["title"].Value.Trim();
        var duration = match.Groups["duration"].Value;
        var author = match.Groups["author"].Value.Trim();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{title}\n{duration}\n{author}"))).ToLowerInvariant();
        return new(key, title, author, duration);
    }

    public static Task<FavoriteSoundsResult> ReadFavoriteSoundsAsync(IPage page, CancellationToken ct)
        => VisitFavoriteSoundsAsync(page, null, ct);

    public static async Task ApplyFavoriteSoundAsync(IPage page, string key, CancellationToken ct)
    {
        await VisitFavoriteSoundsAsync(page, key, ct);
        ct.ThrowIfCancellationRequested();
        var save = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Save( edit)?|Lưu( chỉnh sửa)?)$", RegexOptions.IgnoreCase) });
        await RequireUniqueAsync(save, "nút Lưu chỉnh sửa");
        await save.ClickAsync(new() { Timeout = 5000 });
        await page.Locator("#panel-favorites").WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 30000 });
    }

    private static async Task<FavoriteSoundsResult> VisitFavoriteSoundsAsync(IPage page, string? selectedKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureNoChallengeAsync(page);
        var sounds = await WaitForUploadControlAsync(page, new Regex("^(Sounds|Âm thanh)$", RegexOptions.IgnoreCase), "nút Âm thanh", ct);
        await sounds.ClickAsync(new() { Timeout = 5000 });
        var gotIt = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Got it|Đã hiểu)$", RegexOptions.IgnoreCase) });
        if (await gotIt.CountAsync() == 1 && await gotIt.IsVisibleAsync()) await gotIt.ClickAsync(new() { Timeout = 2000 });
        var favorites = page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("^(Favorites|Favourites|Yêu thích)$", RegexOptions.IgnoreCase) });
        await RequireUniqueAsync(favorites, "tab nhạc Yêu thích");
        await favorites.ClickAsync(new() { Timeout = 5000 });
        var panel = page.Locator("#panel-favorites");
        await RequireUniqueAsync(panel, "danh sách nhạc Yêu thích");
        await page.WaitForFunctionAsync("""
            () => {
                const p = document.getElementById('panel-favorites');
                return p && (p.querySelector('li, [role="listitem"]') || /no (favorites|favourites|sounds|results)|chưa có|không có/i.test(p.innerText));
            }
            """, null, new() { Timeout = 15000 });
        var found = new Dictionary<string, FavoriteSoundDto>(StringComparer.Ordinal);
        bool complete = false;
        // shortcut: giới hạn 40 lượt cuộn/500 bài để không giữ profile vô hạn; UI báo rõ danh sách chưa đầy đủ.
        for (int turn = 0; turn < 40 && found.Count < 500; turn++)
        {
            ct.ThrowIfCancellationRequested();
            await EnsureNoChallengeAsync(page);
            var rows = panel.GetByRole(AriaRole.Listitem);
            var texts = await rows.AllInnerTextsAsync();
            var parsed = texts.Select(ParseFavoriteSound).ToArray();
            foreach (var sound in parsed) found.TryAdd(sound.Key, sound);
            if (selectedKey != null)
            {
                var indexes = parsed.Select((sound, index) => (sound, index)).Where(x => x.sound.Key == selectedKey).ToArray();
                if (indexes.Length > 1) throw new InvalidOperationException("Nhiều âm thanh yêu thích có cùng thông tin; chưa chọn để tránh nhầm bài.");
                if (indexes.Length == 1)
                {

                    var add = rows.Nth(indexes[0].index).GetByRole(AriaRole.Button);
                    await RequireUniqueAsync(add, "nút thêm nhạc yêu thích");
                    await add.ClickAsync(new() { Timeout = 5000 });
                    return new(found.Values.ToArray(), false);
                }
            }
            var before = string.Join("\n", texts);
            var atBottom = await panel.EvaluateAsync<bool>("""
                p => {
                    const row = p.querySelector('li, [role="listitem"]');
                    let s = row?.parentElement ?? p;
                    while (s && s !== document.body && (s.scrollHeight <= s.clientHeight + 1 || !/auto|scroll|overlay/.test(getComputedStyle(s).overflowY))) s = s.parentElement;
                    if (!s || s === document.body) return true;
                    s.scrollTop += Math.max(100, s.clientHeight * .8);
                    return s.scrollTop + s.clientHeight >= s.scrollHeight - 2;
                }
                """);
            bool changed = true;
            try
            {
                await page.WaitForFunctionAsync("""
                    before => {
                        const p = document.getElementById('panel-favorites');
                        return p && [...p.querySelectorAll('li, [role="listitem"]')].map(e => e.innerText).join('\n') !== before;
                    }
                    """, before, new() { Timeout = 2000 });
            }
            catch (TimeoutException) { changed = false; }
            if (atBottom && !changed) { complete = true; break; }
        }
        if (selectedKey != null) throw new InvalidOperationException("Bài đã chọn không còn trong Yêu thích của tài khoản này hoặc chưa tải được; hãy tải lại danh sách nhạc.");
        return new(found.Values.ToArray(), complete);
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
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Mở trình chỉnh sửa của TikTok Studio, tìm sound theo tên, chọn kết quả đầu tiên chứa tên đó rồi lưu.
    /// TikTok tự giới hạn sound theo độ dài video. Thiếu/không duy nhất/không có kết quả thì dừng trước khi đăng.
    /// shortcut: tên nút khớp giao diện EN thật tới bước mở panel; bước chọn/lưu chưa chạy end-to-end, nhãn VI chỉ đoán.
    /// </summary>
    public static async Task ApplySoundAsync(IPage page, string soundName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureNoChallengeAsync(page);
        // Cấu trúc đối chiếu với TikTok Studio thật (EN) ngày 2026-10-30: nút "Sounds" dưới bản xem trước mở trình dựng
        // có ô "Search sounds", kết quả là role=listitem với một nút "+" không tên, và Save/Cancel ở thanh trên.
        var sounds = await WaitForUploadControlAsync(page, new Regex("^(Sounds|Âm thanh)$", RegexOptions.IgnoreCase), "nút Sounds", cancellationToken);
        await sounds.ClickAsync(new() { Timeout = 5000 });

        var search = page.GetByPlaceholder(new Regex("Search sounds?|Tìm (kiếm )?(sound|âm thanh)", RegexOptions.IgnoreCase));
        await RequireUniqueAsync(search, "ô tìm sound");
        // Lần đầu mở, TikTok hiện popup giới thiệu "Phone mode" che trình dựng; có thì đóng.
        var gotIt = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Got it|Đã hiểu)$", RegexOptions.IgnoreCase) });
        try { await gotIt.First.ClickAsync(new() { Timeout = 2000 }); } catch (TimeoutException) { }
        await search.FillAsync(soundName);
        await search.PressAsync("Enter");

        var match = page.GetByRole(AriaRole.Listitem).Filter(new() { HasTextRegex = new Regex(Regex.Escape(soundName), RegexOptions.IgnoreCase) });
        try { await match.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 }); }
        catch (TimeoutException) { throw new InvalidOperationException($"Không tìm thấy sound \"{soundName}\" trên TikTok. Đã dừng trước khi đăng."); }
        var use = match.First.GetByRole(AriaRole.Button);
        await RequireUniqueAsync(use, "nút thêm sound");
        await use.ClickAsync(new() { Timeout = 5000 });

        var save = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Save( edit)?|Lưu( chỉnh sửa)?)$", RegexOptions.IgnoreCase) });
        await RequireUniqueAsync(save, "nút Lưu chỉnh sửa");
        await save.ClickAsync(new() { Timeout = 5000 });
        await search.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 30000 });
        cancellationToken.ThrowIfCancellationRequested();
    }

    // Popup "Continue to post?" hiện khi kiểm tra bản quyền chưa xong; bấm "Post now" để đăng tiếp trong cùng lần submit.
    // shortcut: nhãn VI "Đăng ngay" chỉ đoán, chưa thấy trên TikTok thật; chưa qualify end-to-end trên live TikTok.
    private static async Task ConfirmPostNowAsync(IPage page)
    {
        var postNow = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Post now|Đăng ngay)$", RegexOptions.IgnoreCase) });
        try { await postNow.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 }); }
        catch (TimeoutException) { return; }
        await RequireUniqueAsync(postNow, "nút Post now");
        await postNow.ClickAsync(new() { Timeout = 5000 });
    }

    private static async Task VerifyContentAsync(IPage page, TikTokUploadInput input)
    {
        var caption = page.Locator("[contenteditable=true][role=textbox]");
        if (await caption.CountAsync() == 0) caption = page.Locator("[contenteditable=true]");
        await RequireUniqueAsync(caption, "ô caption trước khi đăng");
        if (NormalizeCaption(await caption.InnerTextAsync()) != NormalizeCaption(input.Caption))
            throw new InvalidOperationException("Caption đã thay đổi trong lúc xử lý video; chưa đăng.");
    }

    private static string NormalizeCaption(string text) => Regex.Replace(text.Replace('\u00a0', ' '), @"\s+", " ").Trim();

    // Đối chiếu TikTok Studio thật 2026-11-03: account mới có notice che toàn bộ control upload khỏi AX tree.
    private static async Task<ILocator> WaitForUploadControlAsync(IPage page, Regex name, string description, CancellationToken ct)
    {
        var control = page.GetByRole(AriaRole.Button, new() { NameRegex = name });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            ct.ThrowIfCancellationRequested();
            await EnsureNoChallengeAsync(page);
            var checks = page.GetByRole(AriaRole.Dialog).Filter(new()
            {
                HasTextRegex = new Regex(@"Turn on automatic content checks\?", RegexOptions.IgnoreCase)
            });
            if (await checks.CountAsync() > 0 && await checks.First.IsVisibleAsync())
            {
                var cancel = checks.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true });
                await RequireUniqueAsync(cancel, "nút Cancel của lời mời kiểm tra tự động");
                await cancel.ClickAsync(new() { Timeout = 5000 });
                await Task.Delay(100, ct);
                continue;
            }
            var introduction = page.Locator(".tutorial-tooltip").Filter(new()
            {
                HasTextRegex = new Regex("New editing features added", RegexOptions.IgnoreCase)
            });
            if (await introduction.CountAsync() > 0 && await introduction.First.IsVisibleAsync())
            {
                var gotIt = introduction.GetByRole(AriaRole.Button, new() { Name = "Got it", Exact = true });
                await RequireUniqueAsync(gotIt, "nút Got it của giới thiệu editor");
                await gotIt.ClickAsync(new() { Timeout = 5000 });
                await Task.Delay(100, ct);
                continue;
            }
            var count = await control.CountAsync();
            if (count > 1) throw new InvalidOperationException($"Giao diện TikTok không khớp: {description} không duy nhất.");
            if (count == 1 && await control.IsVisibleAsync()) return control;
            await Task.Delay(200, ct);
        } while (clock.Elapsed < TimeSpan.FromSeconds(30));
        throw new InvalidOperationException($"Giao diện TikTok không khớp: không thấy {description}.");
    }

    private static async Task RequireUniqueAsync(ILocator locator, string name, bool attached = false)
    {
        try { await locator.First.WaitForAsync(new() { State = attached ? WaitForSelectorState.Attached : WaitForSelectorState.Visible, Timeout = 30000 }); }
        catch (TimeoutException) { throw new InvalidOperationException($"Giao diện TikTok không khớp: không thấy {name}."); }
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
