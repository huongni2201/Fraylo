using Microsoft.Playwright;
using Fraylo.Browser.Publishing;

namespace Fraylo.Facebook.SmokeChecks;

/// <summary>
/// Fixture HTML mô phỏng cấu trúc đã quan sát ở FACEBOOK_UI_EVIDENCE (E3–E12). Không liên hệ Facebook thật;
/// kết quả PASS không chứng minh Facebook thật hoạt động.
/// </summary>
public static class Program
{
    private static int _passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        _passed++;
        Console.WriteLine($"PASS {_passed}: {message}");
    }

    private const string FixtureTemplate = """
        <main>
        __NOTICE__
        <section aria-label="Create a post"><h3>Create a post</h3>
          <div role="button" tabindex="0" onclick="openComposer()">What's on your mind, Test?</div></section></main>
        <div id="root"></div>
        <script>
        window.posts = 0; window.discarded = 0;
        function openComposer() {
          document.getElementById('root').innerHTML = `
          <div role="dialog" aria-label="Create post">
            <div role="dialog" aria-label="Create post"><h2>Create post</h2><button aria-label="Close composer dialog">x</button></div>
            <button aria-label="Edit privacy. Sharing with __AUDIENCE__.">__AUDIENCE__</button>
            <div role="textbox" contenteditable="true" aria-label="What's on your mind, Test?" id="tb"></div>
            <button id="media">Photo/video</button>
            <div id="attached"></div>
            <button id="post" disabled>Post</button>
          </div>
          <div id="draft" role="dialog" aria-label="Save this post as a draft?" hidden>
            <button>Save as draft</button><button id="del">Delete draft</button></div>`;
          const tb = document.getElementById('tb'), post = document.getElementById('post');
          const refresh = () => post.disabled = !(tb.innerText.trim() || document.querySelector('#attached img'));
          tb.addEventListener('input', () => { refresh(); __DROP__ });
          document.getElementById('media').onclick = () => {
            const i = document.createElement('input'); i.type = 'file'; i.hidden = true; i.multiple = true; document.body.appendChild(i);
            i.onchange = () => { const g = document.getElementById('attached'); g.setAttribute('role','group'); g.setAttribute('aria-label','Attached media');
              g.innerHTML = [...i.files].map(f => `<img alt="${f.name}" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=">`).join(''); refresh(); };
          };
          document.addEventListener('keydown', e => { if (e.key === 'Escape') { const d = document.getElementById('draft'); if (d) d.hidden = false; } });
          document.getElementById('del').onclick = () => { window.discarded++; document.getElementById('root').innerHTML = ''; };
          post.onclick = () => { window.posts++; post.disabled = true;
            const a = document.createElement('div'); a.setAttribute('role','alert'); a.textContent = 'Posting'; document.body.appendChild(a);
            setTimeout(() => { a.textContent = '__RESULT__'; }, 300); };
        }
        </script>
        """;

    // result: published | pending | none | rejected. dropText mô phỏng composer làm mất chữ.
    private static string Fixture(string audience = "Only me", string result = "published", bool dropText = false, bool notice = false)
    {
        var text = result switch
        {
            "published" => "Your post has been published",
            "pending" => "Your post is pending approval by admins",
            "rejected" => "Something went wrong. Try again.",
            _ => "Posting"
        };
        return FixtureTemplate
            .Replace("__NOTICE__", notice ? "<div><h3>Remember Password</h3><button onclick=\"this.parentElement.remove();window.noticeClosed=1\">OK</button><button onclick=\"this.parentElement.remove();window.noticeClosed=1\">Not Now</button></div>" : "")
            .Replace("__AUDIENCE__", audience)
            .Replace("__DROP__", dropText ? "setTimeout(() => { tb.innerText = ''; refresh(); }, 50);" : "")
            .Replace("__RESULT__", text);
    }

    private static FacebookPostInput Input(string text, string[]? images = null, string? audience = "Only me") =>
        new("p1", "Facebook (1)", text, images ?? [], audience);

    private static async Task<(FacebookPostResult? Result, Exception? Error, bool Submitted, IPage Page)> RunAsync(
        IBrowser browser, string html, FacebookPostInput input, string url = "https://www.facebook.com/", bool throwInBefore = false, int timeoutMs = 5000)
    {
        var context = await browser.NewContextAsync();
        await context.RouteAsync("**/*", r => r.FulfillAsync(new() { Status = 200, ContentType = "text/html; charset=utf-8", Body = html }));
        var page = await context.NewPageAsync();
        await page.GotoAsync(url);
        var submitted = false;
        try
        {
            var result = await FacebookPoster.PostOnPageAsync(page, input,
                () => { submitted = true; if (throwInBefore) throw new OperationCanceledException(); return Task.CompletedTask; },
                TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None);
            return (result, null, submitted, page);
        }
        catch (Exception ex) { return (null, ex, submitted, page); }
    }

    private static Task<int> Count(IPage page, string name) => page.EvaluateAsync<int>($"() => window.{name} ?? 0");

    public static async Task<int> Main()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var image = Path.Combine(dir, "a.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "msedge" });
            const string text = "Bài thử tiếng Việt #tag 🎬\nDòng hai";

            var ok = await RunAsync(browser, Fixture(notice: true), Input(text, [image]), timeoutMs: 8000);
            Check(ok.Error == null && ok.Result!.Outcome == FacebookPostOutcome.Published, $"Chữ + ảnh, Only me → Published ({ok.Error?.Message})");
            Check(ok.Submitted && await Count(ok.Page, "posts") == 1, "beforeSubmit chạy một lần và Post bấm đúng một lần");
            Check(await Count(ok.Page, "noticeClosed") == 1, "Đóng đúng thông báo Remember Password đã biết");

            var textOnly = await RunAsync(browser, Fixture(), Input("Chỉ chữ"), timeoutMs: 8000);
            Check(textOnly.Result?.Outcome == FacebookPostOutcome.Published, "Bài chỉ chữ → Published");

            var pending = await RunAsync(browser, Fixture(result: "pending"), Input("x", audience: null), timeoutMs: 8000);
            Check(pending.Result?.Outcome == FacebookPostOutcome.PendingApproval, "Thông báo chờ duyệt → PendingApproval, không phải Published");

            var wrongAudience = await RunAsync(browser, Fixture(audience: "Public"), Input("x"));
            Check(wrongAudience.Error is FacebookPostException { ReasonCode: "audience_mismatch" } && !wrongAudience.Submitted
                && await Count(wrongAudience.Page, "posts") == 0, "Sai đối tượng xem → dừng trước khi đăng");
            Check(await Count(wrongAudience.Page, "discarded") == 1, "Dừng sớm thì bấm Delete draft, không lưu nháp");

            var dropped = await RunAsync(browser, Fixture(dropText: true), Input("Chữ sẽ bị mất"));
            Check(dropped.Error is FacebookPostException { ReasonCode: "content_mismatch" } && !dropped.Submitted, "Composer mất chữ → content_mismatch, chưa đăng");

            var cancelled = await RunAsync(browser, Fixture(), Input("hủy"), throwInBefore: true);
            Check(cancelled.Error is OperationCanceledException && await Count(cancelled.Page, "posts") == 0, "Checkpoint từ chối (hủy) → không bấm Post");

            var none = await RunAsync(browser, Fixture(result: "none"), Input("không xác nhận"), timeoutMs: 1500);
            Check(none.Error is FacebookUnknownOutcomeException && await Count(none.Page, "posts") == 1,
                "Bấm Đăng mà không có xác nhận → Unknown, không gửi lại");

            var rejected = await RunAsync(browser, Fixture(result: "rejected"), Input("bị từ chối"), timeoutMs: 8000);
            Check(rejected.Error is FacebookPostException { ReasonCode: "rejected_by_facebook" }, "Facebook báo lỗi rõ ràng → rejected_by_facebook");

            var login = await RunAsync(browser, Fixture(), Input("x"), url: "https://www.facebook.com/login/");
            Check(login.Error is FacebookNeedsUserException { ReasonCode: "session_expired" } && !login.Submitted, "Trang /login → NeedsUser(session_expired)");
            var checkpoint = await RunAsync(browser, Fixture(), Input("x"), url: "https://www.facebook.com/checkpoint/123");
            Check(checkpoint.Error is FacebookNeedsUserException { ReasonCode: "checkpoint" }, "Trang /checkpoint → NeedsUser(checkpoint)");

            var empty = await RunAsync(browser, Fixture(), Input(""));
            Check(empty.Error is ArgumentException, "Bài rỗng bị từ chối");

            Console.WriteLine($"Facebook adapter smoke: {_passed} PASS (fixture, live NOT_RUN)");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
