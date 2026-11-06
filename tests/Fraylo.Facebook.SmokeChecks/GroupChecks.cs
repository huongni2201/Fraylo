using Fraylo.Browser;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Publishing;
using Fraylo.Browser.Probers;
using Microsoft.Playwright;

namespace Fraylo.Facebook.SmokeChecks;

internal static class GroupChecks
{
    private const string Html = """
        <main role="main"><h1>Test group</h1><button>Joined</button><button onclick="openComposer()">Write something...</button></main>
        <div id="root"></div>
        <script>
        function openComposer(){document.getElementById('root').innerHTML=`
          <div role="dialog" aria-label="Create post">
          <div role="dialog" aria-label="Create post"><h2>Create post</h2></div>
          <span role="switch" aria-label="Post anonymously" aria-checked="__ANON__"></span>
          <a href="https://web.facebook.com/__ACTOR__/">Test's Timeline</a>
          <button aria-label="Edit privacy. Sharing with Public group.">Public group</button>
          <div contenteditable="true" role="textbox" id="text"></div>
          <button id="attach">Photo/video</button><div id="media"></div>
          <button id="post">Post</button></div>`;
          document.getElementById('attach').onclick=()=>{const i=document.createElement('input');i.type='file';i.multiple=true;i.hidden=true;document.querySelector('#root [role=dialog]').appendChild(i);i.onchange=()=>{const g=document.getElementById('media');g.setAttribute('role','group');g.setAttribute('aria-label','Attached media');g.innerHTML=[...i.files].map(f=>`<img alt="${f.name}">`).join('');};};
          document.getElementById('post').onclick=()=>{fetch('/test-post');const a=document.createElement('div');a.setAttribute('role','alert');a.textContent='Your post has been published';document.body.appendChild(a);};
          document.addEventListener('keydown',e=>{if(e.key==='Escape')document.getElementById('root').innerHTML='';});
        }
        </script>
        """;

    public static async Task RunAsync(IBrowser browser, string image, Action<bool, string> check)
    {
        check(FacebookGroupBrowser.NormalizeGroupUrl("https://web.facebook.com/groups/Test/?ref=abc") == "https://www.facebook.com/groups/test/", "Group URL chuẩn hóa host, slug và bỏ tracking");
        check(!FacebookPlatformProber.IsLoginOrCheckpointUrl("https://facebook.com/groups/login-community/") &&
            !FacebookPlatformProber.IsLoginOrCheckpointUrl("https://facebook.com/groups/checkpoint-community/") &&
            FacebookPlatformProber.IsLoginOrCheckpointUrl("https://facebook.com/login/?next=/groups/a/"), "Tên nhóm chứa login/checkpoint không bị nhận nhầm thành trang xác minh");
        foreach (var bad in new[] { "https://evil.test/groups/123/", "javascript:alert(1)", "https://user@facebook.com/groups/123/", "https://facebook.com/groups/joins/", "https://facebook.com/groups/123/posts/456/" })
        {
            try { FacebookGroupBrowser.NormalizeGroupUrl(bad); throw new Exception("URL was accepted"); }
            catch (ArgumentException) { }
        }
        check(true, "Group URL từ chối host lạ, userinfo, script, trang tổng hợp và link bài");
        foreach (var actor in new[] { "tester", "wrong" })
        {
            await using var context = await browser.NewContextAsync();
            var posts = 0;
            await context.AddCookiesAsync([new() { Name = "c_user", Value = "1", Url = "https://www.facebook.com/" }, new() { Name = "xs", Value = "fixture", Url = "https://www.facebook.com/" }]);
            await context.RouteAsync("**/*", async route =>
            {
                var path = new Uri(route.Request.Url).AbsolutePath;
                if (path == "/me/") { await route.FulfillAsync(new() { Status = 302, Headers = new Dictionary<string, string> { { "location", "https://www.facebook.com/tester/" } } }); return; }
                if (path == "/test-post") posts++;
                await route.FulfillAsync(new() { ContentType = "text/html", Body = Html.Replace("__ACTOR__", actor).Replace("__ANON__", "false") });
            });
            using var controller = new FixtureController(context);
            var poster = new FacebookGroupBrowser(controller);
            var checkpoints = 0;
            var input = new FacebookGroupPostInput("test-profile", "Facebook (1)", "https://www.facebook.com/groups/test/", "Test group", "Tiếng Việt 🎬\nDòng hai", [image]);
            if (actor == "tester")
            {
                var loginBlocked = false;
                var result = await poster.PostGroupAsync(input, async () =>
                {
                    checkpoints++;
                    using var login = new LoginCoordinator(processController: controller, canReplaceHeadlessContext: _ => true);
                    try { await login.RequestLoginAsync("test-profile"); }
                    catch (InvalidOperationException) { loginBlocked = true; }
                }, CancellationToken.None);
                check(loginBlocked && controller.Closed == 0, "Đăng nhập không đóng context khi bài nhóm đang chuẩn bị/gửi");
                check(result.Outcome == FacebookPostOutcome.Published && posts == 1 && checkpoints == 1, "Luồng nhóm đầy đủ: /me → đúng actor → ảnh/chữ → checkpoint → đúng một lần Post");
                try { await poster.PostGroupAsync(input, () => throw new OperationCanceledException(), CancellationToken.None); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { }
                check(posts == 1, "CAS/checkpoint từ chối không bấm Post lần nữa");
            }
            else
            {
                try { await poster.PostGroupAsync(input, () => { checkpoints++; return Task.CompletedTask; }, CancellationToken.None); throw new Exception("Expected identity rejection"); }
                catch (FacebookPostException ex) when (ex.ReasonCode == "identity_mismatch") { }
                check(posts == 0 && checkpoints == 0, "Actor sai dừng trước checkpoint và trước Post");
            }
        }
        await using (var context = await browser.NewContextAsync())
        {
            await context.RouteAsync("**/*", r => r.FulfillAsync(new() { ContentType = "text/html", Body = Html.Replace("__ACTOR__", "tester").Replace("__ANON__", "true") }));
            var page = await context.NewPageAsync(); await page.GotoAsync("https://www.facebook.com/groups/test/");
            try { await FacebookGroupBrowser.PrepareGroupOnPageAsync(page, new("p", "Facebook (1)", page.Url, "Test group", "x", []), "https://www.facebook.com/tester/", CancellationToken.None); throw new Exception("Expected anonymous rejection"); }
            catch (FacebookPostException ex) when (ex.ReasonCode == "identity_mismatch") { }
            check(true, "Đăng ẩn danh bị từ chối");
        }
        await using (var context = await browser.NewContextAsync())
        {
            await context.RouteAsync("**/*", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<div role='main'><h2>All groups you've joined (2)</h2><ul><li><a href='/groups/a/'>Group A</a><a href='/groups/a/'>View group</a></li><li><a href='https://web.facebook.com/groups/b/?ref=x'>Group B</a></li><li><a href='https://evil.test/groups/c/'>Evil</a></li></ul></div>" }));
            var page = await context.NewPageAsync(); await page.GotoAsync("https://www.facebook.com/groups/joins/");
            var groups = await FacebookGroupBrowser.ReadJoinedGroupsOnPageAsync(page, CancellationToken.None);
            check(groups.IsComplete && groups.Groups.Count == 2 && groups.Groups.All(g => g.Url.StartsWith("https://www.facebook.com/groups/")), "Danh sách trong role=main: gộp trùng, bỏ host lạ, xác nhận đủ theo tổng");
            await page.GetByRole(AriaRole.Heading).EvaluateAsync("e => e.textContent=\"All groups you've joined (3)\"");
            var partial = await FacebookGroupBrowser.ReadJoinedGroupsOnPageAsync(page, CancellationToken.None);
            check(!partial.IsComplete && partial.Groups.Count == 2, "Facebook dừng tải trước tổng số: giữ danh sách một phần, không báo đã kéo hết");
        }
        await using (var context = await browser.NewContextAsync())
        {
            var page = await context.NewPageAsync();
            foreach (var (text, outcome) in new[] { ("Your post has been published", FacebookPostOutcome.Published), ("Your post is pending approval by admins", FacebookPostOutcome.PendingApproval) })
            {
                await page.SetContentAsync("<div role='alert'>" + text + "</div>");
                var result = await FacebookGroupBrowser.WaitForGroupResultAsync(page, TimeSpan.FromMilliseconds(100));
                check(result.Outcome == outcome, "Thông báo nhóm cụ thể phân biệt " + outcome);
            }
            foreach (var html in new[] { "<div role='status'>2 pending requests</div>", "<div role='alert' hidden>Your post has been published</div>", "<div role='alert'>Your post has been published</div>" })
            {
                await page.SetContentAsync(html);
                try { await FacebookGroupBrowser.WaitForGroupResultAsync(page, TimeSpan.FromMilliseconds(100), new HashSet<string> { "Your post has been published" }); throw new Exception("Expected Unknown"); }
                catch (FacebookUnknownOutcomeException) { }
            }
            check(true, "Không nhận pending chung chung, thông báo ẩn hoặc xác nhận cũ làm kết quả bài mới");
        }
    }

    private sealed class FixtureController(IBrowserContext context) : IBrowserProcessController
    {
        public event EventHandler<string>? ContextClosed { add { } remove { } }
        public IBrowserContextHandle? TryGetContext(string id) => new Handle(context);
        public bool IsRunning(string id) => true;
        public Task<IBrowserContextHandle> LaunchPersistentContextAsync(string id, bool isHeadless, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IBrowserContextHandle> LaunchPersistentContextAsync(string id, bool isHeadless, string? url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task NavigateAsync(string id, string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public int Closed { get; private set; }
        public Task CloseContextAsync(string id, CancellationToken cancellationToken = default) { Closed++; return Task.CompletedTask; }
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Handle(IBrowserContext context) : IBrowserContextHandle
    {
        public string ProfileId => "test-profile";
        public IBrowserContext Context => context;
        public IProfileOwnerLock OwnerLock => throw new NotSupportedException();
        public bool IsHeadless => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
