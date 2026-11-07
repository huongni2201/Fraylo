using System.Globalization;
using System.Text.RegularExpressions;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Probers;
using Fraylo.Browser.Publishing;
using Fraylo.Contracts.FacebookAnalytics;
using Microsoft.Playwright;

namespace Fraylo.Browser.Analytics;

public sealed record FacebookDiscoveredPage(string FacebookPageId, string Name, string Url);
public sealed record FacebookContentRead(IReadOnlyList<FacebookContentDto> Content, bool Complete, string Message);
public interface IFacebookInsightsBrowser
{
    Task<IReadOnlyList<FacebookDiscoveredPage>> DiscoverAsync(string profileId, string identity, CancellationToken ct);
    Task<FacebookContentRead> ReadAsync(string profileId, string identity, string pageId, string pageName,
        Action<string> progress, CancellationToken ct);
}

public sealed class FacebookInsightsBrowser(IBrowserProcessController controller) : IFacebookInsightsBrowser
{
    public const int MaxContent = 50;
    private static readonly Regex NumericId = new("^[0-9]{1,30}$", RegexOptions.CultureInvariant);
    public static string Clean(string value) => value.Replace("\u200b", "").Replace("\u00a0", " ").Trim();
    public static bool ValidId(string? id) => id != null && NumericId.IsMatch(id);

    public Task<IReadOnlyList<FacebookDiscoveredPage>> DiscoverAsync(string profileId, string identity, CancellationToken ct) =>
        WithProfileAsync(profileId, identity, async page =>
        {
            await GoAsync(page, "https://www.facebook.com/pages/?category=your_pages", ct);
            await page.GetByRole(AriaRole.Main).Last.GetByText("Trang bạn quản lý", new() { Exact = true })
                .WaitForAsync(new() { Timeout = 20000 }).WaitAsync(ct);
            return await ReadPagesOnPageAsync(page);
        }, ct);

    public static async Task<IReadOnlyList<FacebookDiscoveredPage>> ReadPagesOnPageAsync(IPage page)
    {
        var cards = await page.Locator("a[href*='/ad_center/create/ad/']").EvaluateAllAsync<PageCard[]>("""
            els => els.map(a => {
              let card=a.parentElement;
              for(let n=0;card && n<8;n++,card=card.parentElement) {
                const links=Array.from(card.querySelectorAll('a[href]'));
                const profile=links.filter(e=> e.innerText.trim() && !/\/(latest|ad_center)\//.test(new URL(e.href).pathname));
                const ids=[...new Set(links.map(e=>new URL(e.href).searchParams.get('page_id')||new URL(e.href).searchParams.get('asset_id')).filter(Boolean))];
                if(profile.length===1 && ids.length===1) return { Id:ids[0], Name:profile[0].innerText.trim(), Url:profile[0].href };
              }
              return null;
            }).filter(Boolean)
            """);
        var found = new Dictionary<string, FacebookDiscoveredPage>();
        foreach (var card in cards)
        {
            if (!ValidId(card.Id) || string.IsNullOrWhiteSpace(card.Name) || card.Name.Length > 300) continue;
            var uri = new Uri(card.Url);
            if (uri.Scheme != "https" || !FacebookHost(uri.Host) || !uri.IsDefaultPort || uri.UserInfo.Length != 0) continue;
            var url = new UriBuilder(uri) { Host = "www.facebook.com", Query = uri.AbsolutePath == "/profile.php" ? uri.Query : "", Fragment = "" }.Uri.AbsoluteUri;
            found.TryAdd(card.Id, new(card.Id, card.Name, url));
        }
        if (found.Count == 0) throw new InvalidOperationException("Chưa đọc được Fanpage từ giao diện Facebook tiếng Việt. Kiểm tra quyền quản lý và phiên trong Cài đặt.");
        return found.Values.ToArray();
    }

    public Task<FacebookContentRead> ReadAsync(string profileId, string identity, string pageId, string pageName,
        Action<string> progress, CancellationToken ct)
    {
        if (!ValidId(pageId)) throw new ArgumentException("ID Fanpage không hợp lệ.");
        return WithProfileAsync(profileId, identity, async page =>
        {
            progress("Đang mở nội dung Fanpage");
            await GoAsync(page, "https://business.facebook.com/latest/posts/published_posts?asset_id=" + pageId, ct);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Nội dung", Exact = true }).WaitForAsync(new() { Timeout = 20000 }).WaitAsync(ct);
            await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(?:Trọn đời|(?:7|28|90) ngày qua)$") })
                .WaitForAsync(new() { Timeout = 25000 }).WaitAsync(ct);
            await DismissKnownNoticesAsync(page, ct);
            var activeFilter = page.GetByRole(AriaRole.Combobox, new() { NameRegex = new Regex("^Bộ lọc Đã chọn Bộ lọc:") });
            if (await activeFilter.CountAsync() == 1)
            {
                var clear = page.GetByRole(AriaRole.Link, new() { Name = "Xóa", Exact = true });
                if (await clear.CountAsync() != 1 || await clear.GetAttributeAsync("href") != "#") throw new InvalidOperationException("Không xác minh được nút xóa bộ lọc nội dung; chưa thu thập.");
                await clear.ClickAsync(new() { Timeout = 5000 }).WaitAsync(ct);
            }
            var lifetime = page.GetByRole(AriaRole.Button, new() { Name = "Trọn đời", Exact = true });
            if (await lifetime.CountAsync() == 0)
            {
                var date = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(?:7|28|90) ngày qua$") });
                if (await date.CountAsync() != 1) throw new InvalidOperationException("Không xác minh được phạm vi ngày của Facebook; chưa thu thập.");
                await date.ClickAsync(new() { Timeout = 10000 }).WaitAsync(ct);
                await page.GetByRole(AriaRole.Radio, new() { Name = "Trọn đời", Exact = true }).ClickAsync(new() { Timeout = 10000 }).WaitAsync(ct);
            }
            await lifetime.WaitForAsync(new() { Timeout = 15000 }).WaitAsync(ct);
            await page.GetByRole(AriaRole.Grid, new() { Name = "Bài viết đã đăng", Exact = true })
                .GetByRole(AriaRole.Checkbox, new() { NameRegex = new Regex("^Chọn mục có ID [0-9]+$") }).First
                .WaitForAsync(new() { Timeout = 30000 }).WaitAsync(ct);
            if (Regex.Match(new Uri(page.Url).Query, @"(?:\?|&)asset_id=(\d+)").Groups[1].Value != pageId)
                throw new InvalidOperationException("Facebook đang mở Fanpage khác; đã dừng.");
            return await ReadContentOnPageAsync(page, pageName, progress, ct);
        }, ct);
    }

    public static async Task DismissKnownNoticesAsync(IPage page, CancellationToken ct)
    {
        for (var round = 0; round < 4; round++)
        {
            var dismissed = false;
            foreach (var (title, action) in new[] {
                ("Chúng tôi đã chuyển phần Mục tiêu sang tab Kết quả", "Hiển thị sau"),
                ("Khám phá kế hoạch hàng tuần mới", "Bỏ qua"),
                ("Tab Bài viết & tin giờ có tên là Nội dung", "Bỏ qua") })
            {
                var dialog = page.GetByRole(AriaRole.Dialog).Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = title, Exact = true }) });
                if (await dialog.CountAsync() != 1) continue;
                await dialog.GetByRole(AriaRole.Button, new() { Name = action, Exact = true }).ClickAsync(new() { Timeout = 5000 }).WaitAsync(ct);
                await Task.Delay(400, ct);
                dismissed = true;
            }
            if (!dismissed) break;
        }
    }

    public static async Task<FacebookContentRead> ReadContentOnPageAsync(IPage page, string pageName, Action<string> progress, CancellationToken ct)
    {
        var grid = page.GetByRole(AriaRole.Grid, new() { Name = "Bài viết đã đăng", Exact = true });
        await grid.WaitForAsync(new() { Timeout = 20000 }).WaitAsync(ct);
        await grid.GetByRole(AriaRole.Row).Filter(new() { HasText = pageName }).First.WaitForAsync(new() { Timeout = 20000 }).WaitAsync(ct);
        var headers = (await grid.GetByRole(AriaRole.Columnheader).AllInnerTextsAsync()).Select(Clean).ToArray();
        foreach (var required in new[] { "Tiêu đề", "Ngày đăng", "Lượt xem", "Lượt thích và cảm xúc", "Bình luận", "Lượt chia sẻ" })
            if (headers.Count(h => h == required) != 1) throw new InvalidOperationException("Cột Facebook thay đổi hoặc chưa tải xong; chưa thu thập số liệu.");
        var result = new Dictionary<string, FacebookContentDto>();
        var stalled = 0;
        var invalidRows = 0;
        for (var round = 0; round < 60 && result.Count < MaxContent; round++)
        {
            ct.ThrowIfCancellationRequested();
            EnsurePage(page);
            var rows = await grid.GetByRole(AriaRole.Row).EvaluateAllAsync<ContentRow[]>("""
                els => els.filter(e=>e.querySelector('input[type=checkbox][aria-label],[role=checkbox][aria-label]')).map(e=>({
                  Id:(e.querySelector('input[type=checkbox],[role=checkbox]')?.getAttribute('aria-label')||'').match(/^Chọn mục có ID (\d+)$/)?.[1]||'',
                  Cells:Array.from(e.querySelectorAll('[role=gridcell]')).map(c=>c.innerText)
                })).filter(e=>e.Id)
                """);
            var before = result.Count;
            foreach (var row in rows)
            {
                if (result.Count >= MaxContent) break;
                if (row.Cells.Length != headers.Length) { invalidRows++; continue; }
                var data = headers.Select((h, i) => (h, value: Clean(row.Cells[i]))).Where(x => x.h.Length > 0).ToDictionary(x => x.h, x => x.value);
                var title = data["Tiêu đề"];
                if (!title.Contains(pageName, StringComparison.Ordinal)) { invalidRows++; continue; }
                var kind = title.Split('\n').Select(Clean).Contains("Thước phim") ? "Reel" : "Post";
                var label = data["Ngày đăng"];
                var date = Regex.Match(label, @"^(\d{1,2}) Tháng (\d{1,2}), (\d{4})$");
                string? publishedDate = date.Success && DateOnly.TryParseExact($"{date.Groups[3]}-{date.Groups[2].Value.PadLeft(2, '0')}-{date.Groups[1].Value.PadLeft(2, '0')}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.ToString("yyyy-MM-dd") : null;
                var metrics = new Dictionary<string, FacebookMetric>();
                foreach (var (key, column) in new[] { ("views", "Lượt xem"), ("reactions", "Lượt thích và cảm xúc"), ("comments", "Bình luận"), ("shares", "Lượt chia sẻ") })
                    metrics[key] = ParseMetric(data.GetValueOrDefault(column, ""));
                var average = data.GetValueOrDefault("Thời gian phát video trung bình", "");
                metrics["averageWatchSeconds"] = ParseSeconds(average);
                var totalWatch = data.GetValueOrDefault("Thời gian xem", "");
                metrics["totalWatchSeconds"] = new(null, totalWatch, MissingReason: ParseMetric(totalWatch).Value == null ? "unavailable" : "unit_unverified");
                var caption = title.Split('\n').Select(Clean).Where(t => t.Length > 0 && t != pageName && t is not ("Thước phim" or "Quảng bá" or "Mở menu thả xuống" or "Facebook"));
                var captionText = string.Join("\n", caption);
                result[row.Id] = new(row.Id, kind, captionText[..Math.Min(captionText.Length, 5000)],
                    kind == "Reel" ? "https://www.facebook.com/reel/" + row.Id + "/" : "https://www.facebook.com/" + row.Id,
                    label, publishedDate, metrics);
            }
            progress($"Đã đọc {result.Count} nội dung (tối đa {MaxContent})");
            stalled = before == result.Count ? stalled + 1 : 0;
            if (stalled >= 4) break;
            // Cuộn vùng chứa grid đã quan sát, không bấm các nút đăng/quảng bá trong dòng.
            await grid.EvaluateAsync("""
                e => { for(let p=e;p;p=p.parentElement) { if(p.scrollHeight>p.clientHeight+10 && /auto|scroll/.test(getComputedStyle(p).overflowY)) { p.scrollTop+=Math.max(500,p.clientHeight-100); return; } } window.scrollBy(0,700); }
                """);
            await Task.Delay(700, ct);
        }
        if (result.Count == 0)
        {
            // Facebook dùng cùng heading cho empty và lỗi; không báo Complete/0 từ thông báo này.
            throw new InvalidOperationException("Facebook không trả nội dung trong phạm vi Trọn đời hoặc bảng chưa tải được. Dữ liệu đã lưu trước đó được giữ nguyên.");
        }
        var missing = result.Values.Any(r => new[] { "views", "reactions", "comments", "shares" }.Any(k => r.Metrics[k].Value == null));
        return new(result.Values.ToArray(), !missing && invalidRows == 0 && result.Count < MaxContent,
            $"Đã đọc {result.Count} nội dung trong phạm vi Trọn đời; tối đa {MaxContent}." + (missing ? " Một số chỉ số Facebook không cung cấp." : "") +
            (invalidRows > 0 ? " Có dòng không đọc được." : "") + " Không xác nhận đã tải hết toàn Fanpage.");
    }

    public static FacebookMetric ParseMetric(string raw)
    {
        var text = Clean(raw);
        if (text.Length == 0 || Regex.IsMatch(text, @"^[\s\-‑–—]+$")) return new(null, raw, MissingReason: "unavailable");
        // Tổng trên dòng đầu, nhãn paid trên dòng sau; không cộng paid vào tổng lần nữa.
        text = Regex.Replace(text, @"\s+\d[\d.,]*\s+từ quảng cáo$", "").Trim();
        var match = Regex.Match(text, @"^(\d+(?:,\d+)?)([KM])$");
        if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture, out var compact) && compact <= 9007199254)
            return new(compact * (match.Groups[2].Value == "K" ? 1000 : 1000000), raw, true);
        if (Regex.IsMatch(text, @"^(?:\d+|\d{1,3}(?:\.\d{3})+)$") && double.TryParse(text.Replace(".", ""), CultureInfo.InvariantCulture, out var number) && number <= 9007199254740991)
            return new(number, raw);
        return new(null, raw, MissingReason: "unrecognized_format");
    }

    public static FacebookMetric ParseSeconds(string raw)
    {
        var match = Regex.Match(Clean(raw), @"^(\d+(?:,\d+)?) giây$");
        return match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture, out var seconds)
            ? new(seconds, raw) : new(null, raw, MissingReason: "unavailable_or_unit_unverified");
    }

    private async Task<T> WithProfileAsync<T>(string profileId, string identity, Func<IPage, Task<T>> action, CancellationToken ct)
    {
        using var lease = FacebookGroupBrowser.AcquireProfileOperation(profileId);
        if (controller.IsLoginWindowOpen(profileId) || controller.IsRunning(profileId))
            throw new InvalidOperationException("Profile Facebook đang có thao tác khác. Hãy chờ hoàn tất và đóng cửa sổ đăng nhập trước.");
        var handle = await controller.LaunchPersistentContextAsync(profileId, true, ct);
        IPage? page = null;
        try
        {
            page = await handle.Context.NewPageAsync();
            await GoAsync(page, "https://www.facebook.com/", ct);
            var cookies = await handle.Context.CookiesAsync(new[] { "https://www.facebook.com/" });
            var actual = FacebookPlatformProber.ReadIdentity(cookies.Select(c => (c.Name, c.Value)));
            var user = cookies.FirstOrDefault(c => c.Name == "c_user")?.Value;
            var actor = cookies.FirstOrDefault(c => c.Name == "i_user")?.Value;
            if (actual == null) throw new FacebookNeedsUserException("session_expired", "Phiên Facebook hết hạn. Đăng nhập lại trong Cài đặt.");
            if (actual != identity || (!string.IsNullOrEmpty(actor) && actor != user)) throw new InvalidOperationException("Danh tính Facebook khác tài khoản đã chọn; chưa đọc số liệu.");
            return await action(page);
        }
        finally
        {
            if (page is { IsClosed: false }) { try { await page.CloseAsync(); } catch (PlaywrightException) { } }
            await controller.CloseContextAsync(profileId, CancellationToken.None);
        }
    }

    private static async Task GoAsync(IPage page, string url, CancellationToken ct)
    {
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).WaitAsync(ct);
        EnsurePage(page);
    }
    private static bool FacebookHost(string host) => host is "facebook.com" or "www.facebook.com" or "web.facebook.com" or "business.facebook.com";
    private static void EnsurePage(IPage page)
    {
        var uri = new Uri(page.Url);
        if (uri.Scheme != "https" || !FacebookHost(uri.Host)) throw new InvalidOperationException("Facebook chuyển ngoài phạm vi được phép; đã dừng.");
        if (FacebookPlatformProber.IsLoginOrCheckpointUrl(page.Url)) throw new FacebookNeedsUserException("session_expired", "Facebook yêu cầu đăng nhập/xác minh trong Cài đặt.");
    }
    public sealed class PageCard
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }
    public sealed class ContentRow
    {
        public string Id { get; set; } = "";
        public string[] Cells { get; set; } = [];
    }
}
