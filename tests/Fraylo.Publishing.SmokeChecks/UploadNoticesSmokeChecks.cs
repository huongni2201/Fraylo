using Microsoft.Playwright;
using Fraylo.Browser.Publishing;

namespace Fraylo.Publishing.SmokeChecks;

public static partial class Program
{
    private const string UploadNoticesFixture = """
        <script>window.cancelled=false;window.confirmed=false;window.gotIt=false;window.posted=false;</script>
        <div id="upload" aria-hidden="true">
          <input type="file" hidden><div role="textbox" contenteditable="true"></div>
          <button onclick="window.posted=true">Post</button>
          <button onclick="document.getElementById('editor').hidden=false">Sounds</button>
          <div id="editor" hidden>
            <button role="tab" onclick="document.getElementById('panel-favorites').hidden=false">Favorites</button>
            <div id="panel-favorites" role="tabpanel" hidden><ul><li>Favorite Beat00:21 · Artist <button>+</button></li></ul></div>
          </div>
        </div>
        <div role="dialog" id="content-check">
          <h2>Turn on automatic content checks?</h2>
          <p>Music copyright check</p><p>Content check lite</p>
          <button onclick="window.cancelled=true;document.getElementById('content-check').hidden=true;document.getElementById('upload').removeAttribute('aria-hidden');document.getElementById('react-joyride-portal').hidden=false;document.getElementById('tutorial').hidden=false">Cancel</button>
          <button onclick="window.confirmed=true">Turn on</button>
        </div>
        <div id="react-joyride-portal" hidden><div style="position:fixed;inset:0;z-index:10;background:rgba(0,0,0,.2)"></div></div>
        <div class="tutorial-tooltip" id="tutorial" hidden style="position:relative;z-index:11">
          <h2>New editing features added</h2>
          <p>Now it's easier than ever before to create professional and engaging videos.</p>
          <button onclick="window.gotIt=true;document.getElementById('react-joyride-portal').hidden=true;document.getElementById('tutorial').hidden=true">Got it</button>
        </div>
        """;

    private static async Task UploadNoticesDomFixtureAsync(IPage page)
    {
        await page.SetContentAsync(UploadNoticesFixture);
        Check(!await page.GetByRole(AriaRole.Button,new(){Name="Sounds",Exact=true}).IsVisibleAsync(),"Đúng bug: notice ẩn nút Sounds khỏi accessibility tree");
        var result=await TikTokVideoUploader.ReadFavoriteSoundsAsync(page,CancellationToken.None);
        Check(result.Sounds.Count==1,"Đọc được Favorites sau đóng notice");
        Check(await page.EvaluateAsync<bool>("() => cancelled && gotIt && !confirmed && !posted"),"Chỉ Cancel kiểm tra tự động và Got it giới thiệu; không Turn on/Post");
        Pass("Regression account mới: notice Turn on và giới thiệu editor không làm mất nút Sounds/Post.");
    }
}
