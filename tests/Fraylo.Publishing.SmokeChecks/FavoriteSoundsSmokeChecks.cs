using Microsoft.Playwright;
using Fraylo.Browser.Publishing;

namespace Fraylo.Publishing.SmokeChecks;

public static partial class Program
{
    private const string FavoriteSoundsFixture = """
        <input type="file" hidden><div role="textbox" contenteditable="true"></div>
        <button onclick="throw new Error('Không được đăng khi đọc Favorites')">Post</button>
        <button onclick="document.getElementById('editor').hidden=false">Âm thanh</button>
        <div id="editor" hidden>
          <button role="tab" onclick="document.getElementById('panel-favorites').hidden=false">Yêu thích</button>
          <div id="panel-favorites" role="tabpanel" hidden>
            <ul>
              <li><span>Âm thanh gốc - Rái cá</span><span>00:15 · Tác giả A</span><button onclick="window.picked='a'"><svg></svg></button></li>
              <li><span>Âm thanh gốc - Rái cá</span><span>00:21 · Tác giả B</span><button onclick="window.picked='b'"><svg></svg></button></li>
            </ul>
          </div>
          <button onclick="document.getElementById('editor').hidden=true">Lưu</button>
        </div>
        """;

    private static async Task FavoriteSoundsDomFixtureAsync(IPage page)
    {
        var first = TikTokVideoUploader.ParseFavoriteSound("Âm thanh gốc - Rái cá00:15 · Tác giả A");
        var second = TikTokVideoUploader.ParseFavoriteSound("Âm thanh gốc - Rái cá\n00:21 · Tác giả B");
        Check(first.Title == second.Title && first.Key != second.Key && second.Duration == "00:21", "Phân biệt bài trùng tên");
        Check(TikTokVideoUploader.ParseFavoriteSound("  Âm thanh gốc - Rái cá\n00:21 ·   Tác giả B ").Key == second.Key, "Chuẩn hóa khoảng trắng");
        await Reject(() => Task.FromResult(TikTokVideoUploader.ParseFavoriteSound("markup không còn thời lượng")));
        await page.SetContentAsync(FavoriteSoundsFixture);
        var result = await TikTokVideoUploader.ReadFavoriteSoundsAsync(page, CancellationToken.None);
        Check(result.IsComplete && result.Sounds.Count == 2 && result.Sounds[1].Key == second.Key, "Đọc đúng metadata Unicode");
        Check(await page.EvaluateAsync<string?>("() => window.picked ?? null") == null, "Đọc danh sách không thêm sound");
        await page.SetContentAsync(FavoriteSoundsFixture);
        await TikTokVideoUploader.ApplyFavoriteSoundAsync(page, second.Key, CancellationToken.None);
        Check(await page.EvaluateAsync<string>("() => window.picked") == "b", "Chọn tác giả/thời lượng đúng dù trùng tên");
        Check(await page.Locator("#editor").IsHiddenAsync(), "Đã lưu đúng sound");
        await page.SetContentAsync(FavoriteSoundsFixture);
        await page.EvaluateAsync("() => { window.picked = null; }");
        await Reject(() => TikTokVideoUploader.ApplyFavoriteSoundAsync(page, new string('c', 64), CancellationToken.None));
        Check(await page.EvaluateAsync<string?>("() => window.picked ?? null") == null, "Bài không còn Favorites thì không tự chọn bài khác");
        Pass("Favorites: metadata Unicode, chọn đúng bài trùng tên và chặn bài đã bị bỏ khỏi danh sách.");

        await page.SetContentAsync(FavoriteSoundsFixture);
        await page.Locator("#panel-favorites ul").EvaluateAsync("e => e.innerHTML = '<li>Âm thanh gốc - Rái cá00:21 · Tác giả B<button>+</button></li><li>Âm thanh gốc - Rái cá00:21 · Tác giả B<button>+</button></li>'");
        await Reject(() => TikTokVideoUploader.ApplyFavoriteSoundAsync(page, second.Key, CancellationToken.None));
        await page.SetContentAsync(FavoriteSoundsFixture);
        await page.Locator("#panel-favorites ul").EvaluateAsync("e => e.innerHTML = 'Chưa có nhạc yêu thích'");
        var empty = await TikTokVideoUploader.ReadFavoriteSoundsAsync(page, CancellationToken.None);
        Check(empty.Sounds.Count == 0 && empty.IsComplete, "Danh sách rỗng thật");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await TikTokVideoUploader.ReadFavoriteSoundsAsync(page, canceled.Token); throw new Exception("Phải hủy"); }
        catch (OperationCanceledException) { }
        Pass("Favorites: không chọn khi metadata trùng hoàn toàn; hỗ trợ danh sách rỗng và hủy.");

        await page.SetContentAsync(FavoriteSoundsFixture);
        await page.Locator("#panel-favorites").EvaluateAsync("""
            p => {
                p.style.cssText='height:90px;overflow-y:auto';
                p.querySelectorAll('li').forEach(li => li.style.height='80px');
                p.addEventListener('scroll', () => {
                    if (p.dataset.loaded) return;
                    p.dataset.loaded='true';
                    setTimeout(() => p.querySelector('ul').insertAdjacentHTML('beforeend', '<li style="height:80px">Bài tải thêm00:30 · Artist C<button>+</button></li>'), 100);
                });
            }
            """);
        var lazy = await TikTokVideoUploader.ReadFavoriteSoundsAsync(page, CancellationToken.None);
        Check(lazy.IsComplete && lazy.Sounds.Count == 3 && lazy.Sounds.Any(s => s.Title == "Bài tải thêm"), $"Cuộn để lấy trang tiếp theo: {lazy.Sounds.Count} bài, complete={lazy.IsComplete}; " + await page.Locator("#panel-favorites").EvaluateAsync<string>("p => JSON.stringify({top:p.scrollTop,height:p.clientHeight,total:p.scrollHeight,text:p.innerText})"));
        Pass("Favorites cuộn và gộp danh sách tải động, không chỉ đọc trang đầu.");
        await UploadNoticesDomFixtureAsync(page);

    }
}
