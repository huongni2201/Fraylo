using System.Text.Json;
using System.Text.RegularExpressions;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Probers;
using Microsoft.Playwright;

namespace Fraylo.Browser.Publishing;

public sealed record FacebookJoinedGroup(string GroupId, string Name, string Url);
public sealed record FacebookGroupDiscovery(IReadOnlyList<FacebookJoinedGroup> Groups, bool IsComplete, string Message);
public sealed record FacebookGroupPostInput(string ProfileId, string AccountIdentity, string GroupUrl,
    string GroupName, string Text, IReadOnlyList<string> ImagePaths, Func<string, string, Task>? ReportStage = null);

public interface IFacebookGroupBrowser
{
    Task<FacebookGroupDiscovery> DiscoverGroupsAsync(string profileId, string accountIdentity, CancellationToken ct);
    Task<FacebookPostResult> PostGroupAsync(FacebookGroupPostInput input, Func<Task> beforeSubmit, CancellationToken ct);
}

/// <summary>Nhóm đã tham gia, chạy ẩn. Hợp đồng EN được quan sát trực tiếp; UI khác thì dừng, không đoán.</summary>
public sealed class FacebookGroupBrowser(IBrowserProcessController controller) : IFacebookGroupBrowser
{
    // EN và VI đã quan sát trên trang groups/joins (heading cấp 2 kèm tổng số nhóm).
    private static readonly Regex GroupsHeading = new(@"^(?:All groups you've joined|Tất cả các nhóm bạn đã tham gia)\s*\(([\d,.]+)\)$", RegexOptions.IgnoreCase);
    private static readonly string[] ViewGroupLinkNames = ["View group", "Xem nhóm"];
    // Tên accessible EN và VI đã quan sát trong ARIA snapshot trang nhóm/composer; khớp toàn chuỗi, không đoán biến thể khác.
    private static readonly Regex JoinedButton = new(@"^(Joined|Đã tham gia)$");
    private static readonly Regex WriteEntryButton = new(@"^(Write something\.\.\.|Bạn viết gì đi\.\.\.)$");
    private static readonly Regex CreatePostDialog = new(@"^(Create post|Tạo bài viết)$");
    private static readonly Regex AttachedMediaGroup = new(@"^(Attached media|File phương tiện đính kèm)$");
    private static readonly Regex PhotoVideoButton = new(@"^(Photo\/video|Ảnh\/video)$"); // Playwright nhúng regex vào selector, dấu / phải thoát.
    private static readonly Regex PostButton = new(@"^(Post|Đăng)$");
    private static readonly Regex AnonymousSwitch = new(@"^(Post anonymously|Đăng ẩn danh)$");
    private static readonly Regex ActorTimelineLink = new(@"(^Dòng thời gian của |'s Timeline$)", RegexOptions.IgnoreCase);
    // "Nhóm riêng tư" chưa quan sát trực tiếp; nếu sai thì chỉ dừng trước khi đăng.
    private static readonly Regex GroupAudienceButton = new(@"^(Edit privacy\. Sharing with (Public|Private) group\.|Chỉnh sửa quyền riêng tư\. Đang chia sẻ với Nhóm (công khai|riêng tư)\.)$", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase)
        { "joins", "feed", "discover", "create", "notifications", "search", "your_groups" };

    public static string NormalizeGroupUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !IsFacebookHost(uri.Host) || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new ArgumentException("URL nhóm phải là HTTPS trên facebook.com.");
        var match = Regex.Match(uri.AbsolutePath, @"^/groups/([A-Za-z0-9._-]+)/?$", RegexOptions.CultureInvariant);
        if (!match.Success || ReservedPaths.Contains(match.Groups[1].Value))
            throw new ArgumentException("Cần URL của một nhóm Facebook cụ thể.");
        return "https://www.facebook.com/groups/" + match.Groups[1].Value.ToLowerInvariant() + "/";
    }

    public async Task<FacebookGroupDiscovery> DiscoverGroupsAsync(string profileId, string accountIdentity, CancellationToken ct) =>
        await WithProfileAsync(profileId, accountIdentity, async (page, _) =>
        {
            await page.GotoAsync("https://www.facebook.com/groups/joins/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            EnsureSessionPage(page);
            return await ReadJoinedGroupsOnPageAsync(page, ct);
        }, ct);

    public static async Task<FacebookGroupDiscovery> ReadJoinedGroupsOnPageAsync(IPage page, CancellationToken ct)
    {
        EnsureSessionPage(page);
        var heading = page.GetByRole(AriaRole.Heading, new() { NameRegex = GroupsHeading });
        try { await heading.WaitForAsync(new() { Timeout = 20000 }); }
        catch (TimeoutException) { throw new FacebookPostException("group_list_not_found", "Không thấy danh sách nhóm đã tham gia. Hiện hỗ trợ giao diện Facebook tiếng Anh hoặc tiếng Việt."); }
        if (await heading.CountAsync() != 1) throw new FacebookPostException("group_list_not_found", "Danh sách nhóm không duy nhất; chưa đồng bộ.");
        var countMatch = GroupsHeading.Match((await heading.InnerTextAsync()).Trim());
        if (!int.TryParse(countMatch.Groups[1].Value.Replace(",", "").Replace(".", ""), out var expected))
            throw new FacebookPostException("group_list_not_found", "Không đọc được tổng số nhóm Facebook.");
        var main = heading.Locator("xpath=ancestor::*[self::main or @role='main'][1]");
        var groups = new Dictionary<string, FacebookJoinedGroup>(StringComparer.Ordinal);
        var unchanged = 0;
        for (var round = 0; round < 400; round++)
        {
            ct.ThrowIfCancellationRequested();
            EnsureSessionPage(page);
            var previous = groups.Count;
            var links = await main.GetByRole(AriaRole.Listitem).GetByRole(AriaRole.Link)
                .EvaluateAllAsync<JsonElement>("els => els.map(e => ({url:e.href,name:e.innerText.trim()}))");
            foreach (var item in links.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString();
                var url = item.GetProperty("url").GetString();
                if (string.IsNullOrWhiteSpace(name) || ViewGroupLinkNames.Contains(name) || string.IsNullOrWhiteSpace(url)) continue;
                try
                {
                    var normalized = NormalizeGroupUrl(url);
                    var id = new Uri(normalized).Segments[2].TrimEnd('/');
                    groups.TryAdd(normalized, new(id, name, normalized));
                }
                catch (ArgumentException) { }
            }
            if (groups.Count == expected)
                return new(groups.Values.ToArray(), true, $"Đã tải đủ {groups.Count} nhóm đã tham gia.");
            unchanged = previous == groups.Count ? unchanged + 1 : 0;
            if (unchanged >= 8) break;
            var rows = main.GetByRole(AriaRole.Listitem);
            if (await rows.CountAsync() == 0) break;
            await rows.Last.ScrollIntoViewIfNeededAsync(new() { Timeout = 5000 });
            await rows.Last.EvaluateAsync("e => { let p=e.parentElement; while(p && p.scrollHeight <= p.clientHeight+1) p=p.parentElement; if(p) p.scrollTop=p.scrollHeight; }");
            await page.WaitForTimeoutAsync(750);
        }
        return new(groups.Values.ToArray(), false, $"Đã đọc {groups.Count}/{expected} nhóm. Facebook chưa tải hết; tải lại để tiếp tục, chưa thể xác nhận toàn bộ.");
    }

    public async Task<FacebookPostResult> PostGroupAsync(FacebookGroupPostInput input, Func<Task> beforeSubmit, CancellationToken ct)
    {
        ValidateContent(input);
        var url = NormalizeGroupUrl(input.GroupUrl);
        return await WithProfileAsync(input.ProfileId, input.AccountIdentity, async (page, context) =>
        {
            if (input.ReportStage != null) await input.ReportStage("checking_target", "Đang xác minh tài khoản và mở nhóm cần đăng.");
            // /me lấy đúng URL actor của phiên; không suy danh tính từ tên hiển thị có thể trùng.
            await page.GotoAsync("https://www.facebook.com/me/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            EnsureSessionPage(page);
            await page.WaitForURLAsync(u => !new Uri(u).AbsolutePath.TrimEnd('/').Equals("/me", StringComparison.OrdinalIgnoreCase), new() { Timeout = 15000, WaitUntil = WaitUntilState.DOMContentLoaded });
            var actorUrl = page.Url;
            _ = NormalizeActorUrl(actorUrl);
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            var ownsDraft = false;
            var submitted = false;
            try
            {
                var dialog = await PrepareGroupOnPageAsync(page, input, actorUrl, ct, () => ownsDraft = true);
                var post = dialog.GetByRole(AriaRole.Button, new() { NameRegex = PostButton });
                ct.ThrowIfCancellationRequested();
                await EnsureIdentityAsync(context, input.AccountIdentity);
                var previousMessages = (await page.GetByRole(AriaRole.Alert).Or(page.GetByRole(AriaRole.Status)).AllInnerTextsAsync()).Select(t => t.Trim()).ToHashSet(StringComparer.Ordinal);
                var feedProbe = FeedProbeText(input.Text);
                var feedBefore = feedProbe == null ? 0 : await CountFeedMatchesAsync(page, feedProbe);
                await beforeSubmit();
                submitted = true;
                try
                {
                    // Sau checkpoint, mọi lỗi click/timeout đều có thể đã gửi: tuyệt đối không retry.
                    await post.ClickAsync(new() { Timeout = 5000 });
                    return await WaitForGroupResultAsync(page, TimeSpan.FromSeconds(60), previousMessages, feedProbe, feedBefore);
                }
                catch (FacebookPostException) { throw; }
                catch (FacebookUnknownOutcomeException) { throw; }
                catch (Exception) { throw new FacebookUnknownOutcomeException("Đã bắt đầu gửi bài nhưng mất xác nhận. Kiểm tra nhóm thủ công trước khi đăng lại."); }
            }
            finally
            {
                if (ownsDraft && !submitted) await DiscardGroupDraftAsync(page);
            }
        }, ct);
    }

    public static async Task<ILocator> PrepareGroupOnPageAsync(IPage page, FacebookGroupPostInput input, string actorUrl, CancellationToken ct, Action? onEmptyComposer = null)
    {
        ValidateContent(input);
        EnsureSessionPage(page);
        if (NormalizeGroupUrl(page.Url) != NormalizeGroupUrl(input.GroupUrl))
            throw new FacebookPostException("target_mismatch", "Trang hiện tại không phải nhóm đã chọn; chưa đăng.");
        var heading = page.GetByRole(AriaRole.Heading, new() { Level = 1, Name = input.GroupName, Exact = true });
        await heading.WaitForAsync(new() { Timeout = 20000 });
        if (await heading.CountAsync() != 1 || await page.GetByRole(AriaRole.Button, new() { NameRegex = JoinedButton }).CountAsync() != 1)
            throw new FacebookPostException("target_not_found", "Nhóm đổi tên, chưa tham gia hoặc không có quyền đăng; hãy đồng bộ lại.");
        if (input.ReportStage != null) await input.ReportStage("opening_composer", "Đang mở khung soạn bài trong nhóm.");
        var entry = page.GetByRole(AriaRole.Button, new() { NameRegex = WriteEntryButton });
        if (await entry.CountAsync() != 1) throw new FacebookPostException("composer_not_found", "Không thấy duy nhất một nút soạn bài trong nhóm.");
        await entry.ClickAsync(new() { Timeout = 10000 });
        var dialog = page.GetByRole(AriaRole.Dialog, new() { NameRegex = CreatePostDialog })
            .Filter(new() { Has = page.GetByRole(AriaRole.Textbox) });
        await dialog.WaitForAsync(new() { Timeout = 20000 });
        if (await dialog.CountAsync() != 1) throw new FacebookPostException("composer_not_found", "Composer nhóm không duy nhất; chưa đăng.");
        await VerifyActorAsync(dialog, actorUrl);
        var initialText = dialog.GetByRole(AriaRole.Textbox);
        if (await initialText.CountAsync() != 1 || !string.IsNullOrWhiteSpace(await initialText.InnerTextAsync()) ||
            await dialog.GetByRole(AriaRole.Group, new() { NameRegex = AttachedMediaGroup }).CountAsync() != 0)
            throw new FacebookPostException("composer_not_empty", "Composer có bản nháp sẵn; đã dừng để giữ nội dung cũ.");
        onEmptyComposer?.Invoke();
        if (input.ImagePaths.Count > 0)
        {
            if (input.ReportStage != null) await input.ReportStage("uploading_media", "Đang đính kèm ảnh vào bài đăng.");
            await dialog.GetByRole(AriaRole.Button, new() { NameRegex = PhotoVideoButton }).ClickAsync();
            // E7: Facebook có hai input; chọn input trong composer thay vì input bất kỳ trên trang.
            var files = dialog.Locator("input[type=file]");
            await files.First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 });
            if (await files.CountAsync() != 1) throw new FacebookPostException("media_rejected", "Không xác định duy nhất nơi nhận ảnh trong composer.");
            await files.SetInputFilesAsync(input.ImagePaths);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (await AttachedMediaCountAsync(dialog) != input.ImagePaths.Count)
            {
                if (DateTime.UtcNow > until) throw new FacebookPostException("media_rejected", "Nhóm chưa nhận đủ ảnh; chưa đăng.");
                await page.WaitForTimeoutAsync(500);
            }
        }
        var textbox = dialog.GetByRole(AriaRole.Textbox);
        if (await textbox.CountAsync() != 1) throw new FacebookPostException("composer_not_found", "Composer có nhiều ô nhập; chưa đăng.");
        if (input.ReportStage != null) await input.ReportStage("checking_composer", "Đang điền và kiểm tra nội dung trước khi gửi.");
        await textbox.FillAsync(input.Text);
        if (NormalizeText(await textbox.InnerTextAsync()) != NormalizeText(input.Text))
            throw new FacebookPostException("content_mismatch", "Nội dung trong composer không khớp; chưa đăng.");
        if (await AttachedMediaCountAsync(dialog) != input.ImagePaths.Count) throw new FacebookPostException("media_mismatch", "Số ảnh trong composer không khớp; chưa đăng.");
        await VerifyActorAsync(dialog, actorUrl);
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { NameRegex = PostButton })).ToBeEnabledAsync(new() { Timeout = 30000 });
        EnsureSessionPage(page);
        ct.ThrowIfCancellationRequested();
        return dialog;
    }

    // Facebook hiện tối đa 5 ảnh; từ ảnh thứ 6 ô cuối phủ "+N" với N = tổng − 4 (quan sát live với 6 và 8 ảnh, VI).
    internal static async Task<int> AttachedMediaCountAsync(ILocator dialog)
    {
        var group = dialog.GetByRole(AriaRole.Group, new() { NameRegex = AttachedMediaGroup });
        if (await group.CountAsync() != 1) return 0;
        var visible = await group.Locator("img").CountAsync();
        var overflow = group.GetByText(new Regex(@"^\+(\d+)$"));
        if (await overflow.CountAsync() != 1) return visible;
        return 4 + int.Parse((await overflow.InnerTextAsync()).Trim().TrimStart('+'));
    }

    private static async Task VerifyActorAsync(ILocator dialog, string actorUrl)
    {
        var anonymous = dialog.GetByRole(AriaRole.Switch, new() { NameRegex = AnonymousSwitch });
        if (await anonymous.CountAsync() > 0 && await anonymous.GetAttributeAsync("aria-checked") != "false")
            throw new FacebookPostException("identity_mismatch", "Composer đang đăng ẩn danh hoặc chưa xác định danh tính; chưa đăng.");
        var actors = dialog.GetByRole(AriaRole.Link, new() { NameRegex = ActorTimelineLink });
        if (await actors.CountAsync() != 1 || NormalizeActorUrl(await actors.GetAttributeAsync("href") ?? "") != NormalizeActorUrl(actorUrl))
            throw new FacebookPostException("identity_mismatch", "Danh tính trong composer khác tài khoản đã chọn; chưa đăng.");
        var audience = dialog.GetByRole(AriaRole.Button, new() { NameRegex = GroupAudienceButton });
        if (await audience.CountAsync() != 1) throw new FacebookPostException("target_mismatch", "Composer không xác nhận đăng vào nhóm; chưa đăng.");
    }

    // Giao diện VI chỉ hiện "Đang đăng" rồi bài xuất hiện trong nhóm (quan sát live), không có thông báo thành công cụ thể.
    // Bằng chứng thay thế: đoạn chữ đầu của bài xuất hiện thêm trong nội dung nhóm ngoài hộp thoại soạn bài.
    public static string? FeedProbeText(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var segment = Regex.Split(line, @"[^\p{L}\p{N}\p{P}\p{Zs}]+").Select(s => s.Trim()).OrderByDescending(s => s.Length).FirstOrDefault() ?? "";
        segment = Regex.Replace(segment, @"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$", "");
        return segment.Length >= 12 ? segment : null;
    }

    public static async Task<int> CountFeedMatchesAsync(IPage page, string probe) =>
        await page.GetByText(probe).EvaluateAllAsync<int>("els => els.filter(e => !e.closest('[role=dialog]') && e.getClientRects().length > 0).length");

    public static async Task<FacebookPostResult> WaitForGroupResultAsync(IPage page, TimeSpan timeout, IReadOnlySet<string>? previousMessages = null, string? feedProbe = null, int feedBefore = 0)
    {
        var until = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < until)
            {
                var alerts = page.GetByRole(AriaRole.Alert).Or(page.GetByRole(AriaRole.Status));
                for (var i = 0; i < await alerts.CountAsync(); i++)
                {
                    var alert = alerts.Nth(i);
                    if (!await alert.IsVisibleAsync()) continue;
                    var text = (await alert.InnerTextAsync()).Trim();
                    if (previousMessages?.Contains(text) == true) continue;
                    // Chỉ chấp nhận thông báo cụ thể. Không dùng "pending" hoặc dialog đóng làm thành công.
                    if (Regex.IsMatch(text, @"^Your post (is pending approval by admins|has been submitted for admin approval)[.!]?$", RegexOptions.IgnoreCase))
                        return new(FacebookPostOutcome.PendingApproval, null, text);
                    if (Regex.IsMatch(text, @"^Your post has been published[.!]?$", RegexOptions.IgnoreCase))
                        return new(FacebookPostOutcome.Published, null, text);
                    if (Regex.IsMatch(text, @"^(We couldn't publish your post|Your post couldn't be published)[.!]?$", RegexOptions.IgnoreCase))
                        throw new FacebookPostException("rejected_by_facebook", text);
                }
                if (feedProbe != null && await CountFeedMatchesAsync(page, feedProbe) > feedBefore)
                    return new(FacebookPostOutcome.Published, null, "Bài đã hiển thị trong nhóm.");
                await page.WaitForTimeoutAsync(500);
            }
        }
        catch (PlaywrightException) { throw new FacebookUnknownOutcomeException("Mất kết nối sau khi gửi bài vào nhóm; kiểm tra thủ công, không tự gửi lại."); }
        throw new FacebookUnknownOutcomeException("Chưa có xác nhận đăng hoặc chờ duyệt từ nhóm; kiểm tra thủ công, không tự gửi lại.");
    }

    public static async Task DiscardGroupDraftAsync(IPage page)
    {
        try
        {
            var composer = page.GetByRole(AriaRole.Dialog, new() { NameRegex = CreatePostDialog })
                .Filter(new() { Has = page.GetByRole(AriaRole.Textbox) });
            if (await composer.CountAsync() != 1) return;
            await page.Keyboard.PressAsync("Escape");
            // Group EN đóng trực tiếp qua Escape (G4); profile composer có thể hỏi Delete draft.
            var delete = page.GetByRole(AriaRole.Button, new() { Name = "Delete draft", Exact = true });
            if (await delete.CountAsync() == 1 && await delete.IsVisibleAsync()) await delete.ClickAsync(new() { Timeout = 3000 });
        }
        catch (PlaywrightException) { }
    }

    private async Task<T> WithProfileAsync<T>(string profileId, string identity, Func<IPage, IBrowserContext, Task<T>> action, CancellationToken ct)
    {
        var gate = FacebookPoster.ProfileOperations.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var owns = false;
        IPage? page = null;
        try
        {
            if (controller.IsLoginWindowOpen(profileId)) throw new FacebookNeedsUserException("session_expired", "Hãy hoàn tất và đóng cửa sổ đăng nhập Facebook trước.");
            var handle = controller.TryGetContext(profileId);
            if (handle == null) { handle = await controller.LaunchPersistentContextAsync(profileId, isHeadless: true, cancellationToken: ct); owns = true; }
            if (!handle.IsHeadless) throw new FacebookNeedsUserException("session_expired", "Facebook đang đăng nhập; hãy đóng cửa sổ đăng nhập trước.");
            page = await handle.Context.NewPageAsync();
            await page.GotoAsync("https://www.facebook.com/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            EnsureSessionPage(page);
            await EnsureIdentityAsync(handle.Context, identity);
            return await action(page, handle.Context);
        }
        finally
        {
            if (page is { IsClosed: false }) { try { await page.CloseAsync(); } catch (PlaywrightException) { } }
            if (owns) { try { await controller.CloseContextAsync(profileId, CancellationToken.None); } catch { } }
            gate.Release();
        }
    }

    private static async Task EnsureIdentityAsync(IBrowserContext context, string expected)
    {
        var cookies = await context.CookiesAsync(new[] { "https://www.facebook.com/" });
        var actual = FacebookPlatformProber.ReadIdentity(cookies.Select(c => (c.Name, c.Value)));
        if (actual == null) throw new FacebookNeedsUserException("session_expired", "Phiên Facebook hết hạn; đăng nhập lại trong Cài đặt.");
        var user = cookies.FirstOrDefault(c => c.Name == "c_user")?.Value;
        var actor = cookies.FirstOrDefault(c => c.Name == "i_user")?.Value;
        if (actual != expected || (!string.IsNullOrEmpty(actor) && actor != user))
            throw new FacebookPostException("identity_mismatch", "Phiên đang dùng danh tính khác tài khoản Facebook đã chọn; chưa đăng.");
    }

    private static void EnsureSessionPage(IPage page)
    {
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !IsFacebookHost(uri.Host))
            throw new FacebookPostException("unexpected_navigation", "Facebook chuyển sang trang ngoài phạm vi; đã dừng.");
        if (FacebookPlatformProber.IsLoginOrCheckpointUrl(page.Url))
            throw new FacebookNeedsUserException(page.Url.Contains("/checkpoint", StringComparison.OrdinalIgnoreCase) ? "checkpoint" : "session_expired", "Facebook yêu cầu đăng nhập/xác minh trong Cài đặt.");
    }

    public static IDisposable AcquireProfileOperation(string profileId)
    {
        var gate = FacebookPoster.ProfileOperations.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) throw new InvalidOperationException("Tài khoản đang có thao tác nền; hãy chờ hoàn tất.");
        return new OperationLease(gate);
    }

    private sealed class OperationLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private static bool IsFacebookHost(string host) => host is "facebook.com" or "www.facebook.com" or "web.facebook.com" or "m.facebook.com";
    private static string NormalizeActorUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !IsFacebookHost(uri.Host) || uri.UserInfo.Length != 0)
            throw new FacebookPostException("identity_mismatch", "Không xác minh được URL danh tính Facebook.");
        if (uri.AbsolutePath.Equals("/profile.php", StringComparison.OrdinalIgnoreCase))
        {
            var id = Regex.Match(uri.Query, @"(?:^\?|&)id=(\d+)(?:&|$)").Groups[1].Value;
            if (id.Length == 0) throw new FacebookPostException("identity_mismatch", "URL danh tính thiếu ID.");
            return "/profile.php?id=" + id;
        }
        return uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
    }
    private static string NormalizeText(string text) => text.Replace("\r\n", "\n").Trim();
    private static void ValidateContent(FacebookGroupPostInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Text) && input.ImagePaths.Count == 0) throw new ArgumentException("Bài đăng cần có chữ hoặc ảnh.");
        if (input.ImagePaths.Count > 10 || input.Text.EnumerateRunes().Count() > 10000) throw new ArgumentException("Tối đa 10 ảnh và 10.000 ký tự cho một bài.");
    }
}

