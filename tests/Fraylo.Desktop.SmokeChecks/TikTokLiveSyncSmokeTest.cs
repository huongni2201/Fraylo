using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Fraylo.Browser;
using Fraylo.Browser.Models;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Desktop.SmokeChecks;

public static class TikTokLiveSyncSmokeTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("       Testing TikTok Live Crawler on Real Profile               ");
        Console.WriteLine("=================================================================");

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profileId = "profile_tiktok_3dc15208b53a4fa79b807d66ee003422";
        string realProfileDir = Path.Combine(localAppData, "Fraylo", "profiles", profileId);

        if (!Directory.Exists(realProfileDir))
        {
            Console.WriteLine("[NOT_RUN]: Profile directory not found.");
            return 0;
        }

        string customProfilesDir = Path.Combine(localAppData, "Fraylo", "profiles");
        var tabPool = new TabPool();
        var tempDbPath = Path.Combine(Path.GetTempPath(), "tiktok_live_test_" + Guid.NewGuid().ToString("N") + ".db");
        var bootstrapper = new SqliteDatabaseBootstrapper();
        await bootstrapper.BootstrapAsync(tempDbPath);
        var dbOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<FrayloDbContext>()
            .UseSqlite($"Data Source={tempDbPath}")
            .Options;
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), customProfilesDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "TikTok"));
        await profileRegistry.EnsureProfileAsync(profileId, BrowserProfileKind.TikTok);
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);

        try
        {
            Console.WriteLine("Launching persistent context in headless mode...");
            var handle = await processController.LaunchPersistentContextAsync(
                profileId,
                isHeadless: true,
                initialUrl: "https://www.tiktok.com/",
                cancellationToken: CancellationToken.None);

            var page = handle.Context.Pages.FirstOrDefault() ?? await handle.Context.NewPageAsync();
            Console.WriteLine($"Current URL: {page.Url}");

            // Đợi hydrate
            await page.WaitForTimeoutAsync(3000);

            // Trích xuất danh tính và thông tin cơ bản
            var jsResult = await page.EvaluateAsync<string>(@"async () => {
                let uniqueId = null;
                let secUid = null;
                let nickname = null;
                let totalLikes = null;
                let videoCount = null;
                let followerCount = null;

                try {
                    const res = await fetch('/passport/web/account/info/?aid=1988', { credentials: 'include' });
                    if (res.ok) {
                        const json = await res.json();
                        if (json && json.data) {
                            uniqueId = json.data.username || json.data.unique_id;
                            nickname = json.data.screen_name || json.data.nickname;
                        }
                    }
                } catch (e) {}

                try {
                    const scriptEl = document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__');
                    if (scriptEl && scriptEl.textContent) {
                        const data = JSON.parse(scriptEl.textContent);
                        const defaultScope = data['__DEFAULT_SCOPE__'] || {};
                        const user = defaultScope['webapp.app-context']?.user;
                        if (user) {
                            uniqueId = uniqueId || user.uniqueId;
                            secUid = secUid || user.secUid;
                            nickname = nickname || user.nickname;
                        }
                        const userDetail = defaultScope['webapp.user-detail'];
                        if (userDetail && userDetail.userInfo) {
                            const u = userDetail.userInfo.user;
                            const stats = userDetail.userInfo.stats;
                            if (u) {
                                uniqueId = uniqueId || u.uniqueId;
                                secUid = secUid || u.secUid;
                                nickname = nickname || u.nickname;
                            }
                            if (stats) {
                                totalLikes = stats.heartCount;
                                videoCount = stats.videoCount;
                                followerCount = stats.followerCount;
                            }
                        }
                    }
                } catch (e) {}

                return JSON.stringify({ uniqueId, secUid, nickname, totalLikes, videoCount, followerCount });
            }");

            Console.WriteLine($"Extracted user info: {jsResult}");

            using var doc = JsonDocument.Parse(jsResult);
            string? uniqueId = doc.RootElement.TryGetProperty("uniqueId", out var uEl) ? uEl.GetString() : null;
            string? secUid = doc.RootElement.TryGetProperty("secUid", out var sEl) ? sEl.GetString() : null;

            if (!string.IsNullOrWhiteSpace(uniqueId))
            {
                Console.WriteLine($"Navigating to user profile: https://www.tiktok.com/@{uniqueId} ...");
                await page.GotoAsync($"https://www.tiktok.com/@{uniqueId}", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 20000
                });

                await page.WaitForTimeoutAsync(3000);

                var videosResult = await page.EvaluateAsync<string>(@"async () => {
                    const items = [];
                    // 1. Đọc từ SSR Data
                    try {
                        const scriptEl = document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__');
                        if (scriptEl && scriptEl.textContent) {
                            const data = JSON.parse(scriptEl.textContent);
                            const defaultScope = data['__DEFAULT_SCOPE__'] || {};
                            const userDetail = defaultScope['webapp.user-detail'];
                            const list = userDetail?.itemList || [];
                            for (const item of list) {
                                items.push({
                                    id: item.id,
                                    desc: item.desc || '',
                                    createTime: item.createTime,
                                    views: item.stats?.playCount,
                                    likes: item.stats?.diggCount,
                                    comments: item.stats?.commentCount,
                                    shares: item.stats?.shareCount
                                });
                            }
                        }
                    } catch (e) {}

                    // 2. Đọc từ DOM nếu SSR itemList rỗng
                    if (items.length === 0) {
                        try {
                            const postEls = document.querySelectorAll('[data-e2e=""user-post-item""], a[href*=""/video/""]');
                            postEls.forEach(el => {
                                const link = el.tagName === 'A' ? el : el.querySelector('a[href*=""/video/""]');
                                if (!link) return;
                                const href = link.getAttribute('href') || '';
                                const match = href.match(/\/video\/(\d+)/);
                                if (!match) return;
                                const id = match[1];
                                if (items.some(x => x.id === id)) return;

                                const viewsEl = el.querySelector('[data-e2e=""video-views""], strong');
                                const viewsText = viewsEl ? (viewsEl.textContent || '').trim() : '';
                                const descEl = el.querySelector('img[alt]');
                                const desc = descEl ? (descEl.getAttribute('alt') || '').trim() : '';

                                items.push({
                                    id: id,
                                    desc: desc,
                                    viewsText: viewsText
                                });
                            });
                        } catch (e) {}
                    }

                    return JSON.stringify({ count: items.length, items });
                }");

                Console.WriteLine($"Extracted videos: {videosResult}");
            }

            await processController.CloseContextAsync(profileId);
            Console.WriteLine("[SUCCESS] TikTok live crawl test completed cleanly!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR]: {ex.Message}");
            return 1;
        }
    }
}
