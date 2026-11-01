using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Accounts;
using Fraylo.Contracts.Browser;

namespace Fraylo.Browser.Probers;

/// <summary>
/// Thực hiện thẩm định và trích xuất danh tính phiên làm việc cho nền tảng TikTok (tiktok.com).
/// </summary>
public sealed class TikTokPlatformProber : IPlatformSessionProber
{
    private readonly IProfileRegistry _profileRegistry;

    public TikTokPlatformProber(IProfileRegistry profileRegistry)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
    }

    public string DefaultUrl => ProviderUrlResolver.DefaultTikTokUrl;

    public bool SupportsPlatform(string platformOrProviderKey)
    {
        if (string.IsNullOrWhiteSpace(platformOrProviderKey)) return false;
        var normalized = platformOrProviderKey.Trim().ToLowerInvariant();
        return normalized is "tiktok" or "tiktok-channel" or "tiktok_channel";
    }

    public async Task<ProfileIdentityResolutionResult> ProbeAsync(
        BrowserProfileInfo profile,
        IBrowserProcessController processController,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (processController == null)
        {
            return ProfileIdentityResolutionResult.Failed("Chưa khởi tạo IBrowserProcessController để kiểm tra phiên TikTok.");
        }

        string providerUrl = DefaultUrl;
        string providerHost = new Uri(providerUrl).Host;
        string hostWithoutWww = providerHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? providerHost.Substring(4)
            : providerHost;

        bool isRunning = processController.IsRunning(profile.ProfileId);
        if (isRunning)
        {
            var activeHandle = processController.TryGetContext(profile.ProfileId);
            if (activeHandle != null && activeHandle.Context != null)
            {
                IPage? page = null;
                bool tempPageCreated = false;
                try
                {
                    if (activeHandle.Context.Pages != null)
                    {
                        page = activeHandle.Context.Pages.FirstOrDefault(p => !p.IsClosed && (p.Url.Contains(providerHost, StringComparison.OrdinalIgnoreCase) || p.Url.Contains(hostWithoutWww, StringComparison.OrdinalIgnoreCase)))
                               ?? activeHandle.Context.Pages.FirstOrDefault(p => !p.IsClosed);
                    }
                }
                catch { }

                if (page == null)
                {
                    try
                    {
                        page = await activeHandle.Context.NewPageAsync();
                        tempPageCreated = true;
                    }
                    catch (Exception ex)
                    {
                        return ProfileIdentityResolutionResult.Failed($"Không thể mở tab kiểm tra phiên trên trình duyệt đang hoạt động: {ex.Message}");
                    }
                }

                try
                {
                    return await ExecuteTikTokProbeOnPageAsync(page, activeHandle.Context, profile, cancellationToken);
                }
                finally
                {
                    if (tempPageCreated && !page.IsClosed)
                    {
                        try { await page.CloseAsync(); } catch { }
                    }
                }
            }
        }

        // Nếu trình duyệt đang đóng: Khởi chạy persistent context tạm thời ở chế độ ẩn (isHeadless = true)
        IBrowserContextHandle? tempHandle = null;
        try
        {
            tempHandle = await processController.LaunchPersistentContextAsync(
                profile.ProfileId,
                isHeadless: true,
                initialUrl: providerUrl,
                cancellationToken: cancellationToken);

            IPage? page = null;
            if (tempHandle.Context.Pages != null)
            {
                page = tempHandle.Context.Pages.FirstOrDefault(p => !p.IsClosed);
            }
            page ??= await tempHandle.Context.NewPageAsync();

            return await ExecuteTikTokProbeOnPageAsync(page, tempHandle.Context, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException tex)
        {
            return ProfileIdentityResolutionResult.Failed($"Quá thời gian tải trang TikTok (Timeout): {tex.Message}. Vui lòng kiểm tra mạng và thử lại.");
        }
        catch (PlaywrightException pex) when (pex.Message.Contains("net::") || pex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileIdentityResolutionResult.Failed($"Lỗi kết nối mạng khi tải trang TikTok: {pex.Message}. Vui lòng kiểm tra mạng và thử lại.");
        }
        catch (Exception ex)
        {
            return ProfileIdentityResolutionResult.Failed($"Lỗi khởi chạy kiểm tra phiên TikTok: {ex.Message}");
        }
        finally
        {
            if (tempHandle != null)
            {
                try
                {
                    await processController.CloseContextAsync(profile.ProfileId, CancellationToken.None);
                }
                catch { }
            }
        }
    }

    private async Task<ProfileIdentityResolutionResult> ExecuteTikTokProbeOnPageAsync(
        IPage page,
        IBrowserContext context,
        BrowserProfileInfo profile,
        CancellationToken cancellationToken)
    {
        try
        {
            string currentUrl = page.Url;
            if (!currentUrl.StartsWith("https://www.tiktok.com", StringComparison.OrdinalIgnoreCase) &&
                !currentUrl.StartsWith("https://tiktok.com", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await page.GotoAsync(ProviderUrlResolver.DefaultTikTokUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 15000
                    });
                }
                catch (TimeoutException) { }
                catch (PlaywrightException) { }
            }

            // Đợi trang hydrate và tải cookies/dữ liệu phiên
            await page.WaitForTimeoutAsync(2500);

            string finalUrl = page.Url;

            // 1. Kiểm tra nếu trang điều hướng về trang đăng nhập
            if (finalUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("login.tiktok.com", StringComparison.OrdinalIgnoreCase))
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profile.ProfileId,
                    sessionStatus: BrowserSessionStatus.Unverified,
                    reasonCode: BrowserReasonCode.LoginRequired,
                    cancellationToken: cancellationToken);

                return new ProfileIdentityResolutionResult(
                    IsSuccess: false,
                    ProfileId: profile.ProfileId,
                    Provider: "tiktok",
                    AccountIdentity: null,
                    SessionStatus: BrowserSessionStatus.Unverified,
                    ErrorMessage: "Tài khoản TikTok chưa đăng nhập hoặc phiên đã hết hạn. Vui lòng bấm 'Đăng nhập trình duyệt' để đăng nhập.");
            }

            // 2. Kiểm tra cookies phiên 'sessionid', 'sessionid_ss', 'sid_tt', 'sid_guard'
            var cookies = await context.CookiesAsync(new[] { "https://www.tiktok.com", "https://tiktok.com" });
            bool hasSessionCookie = cookies.Any(c =>
                (c.Name.Equals("sessionid", StringComparison.OrdinalIgnoreCase) ||
                 c.Name.Equals("sessionid_ss", StringComparison.OrdinalIgnoreCase) ||
                 c.Name.Equals("sid_tt", StringComparison.OrdinalIgnoreCase) ||
                 c.Name.Equals("sid_guard", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(c.Value));

            string? uidCookie = cookies.FirstOrDefault(c =>
                c.Name.Equals("uid_tt", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("uid_tt_ss", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

            // 3. Trích xuất danh tính (Unique ID / Handle, Nickname, UID) từ DOM, SSR script, API nội bộ, IndexedDB và LocalStorage
            var jsIdentity = await page.EvaluateAsync<string>(@"async () => {
                let uniqueId = null;
                let nickname = null;
                let uid = null;
                let secUid = null;
                let isLoggedIn = false;
                let hasLoginButton = false;

                // A. Đọc SSR Data script tag: __UNIVERSAL_DATA_FOR_REHYDRATION__
                try {
                    const scriptEl = document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__');
                    if (scriptEl && scriptEl.textContent) {
                        const data = JSON.parse(scriptEl.textContent);
                        const defaultScope = data['__DEFAULT_SCOPE__'] || {};
                        const appContext = defaultScope['webapp.app-context'] || {};
                        const user = appContext.user;
                        if (user) {
                            if (user.uniqueId) uniqueId = String(user.uniqueId).trim();
                            if (user.nickname) nickname = String(user.nickname).trim();
                            if (user.uid) uid = String(user.uid).trim();
                            if (user.secUid) secUid = String(user.secUid).trim();
                            if (typeof user.isLoggedIn === 'boolean') isLoggedIn = user.isLoggedIn;
                        }

                        const userDetail = defaultScope['webapp.user-detail']?.userInfo?.user;
                        if (userDetail) {
                            if (!uniqueId && userDetail.uniqueId) uniqueId = String(userDetail.uniqueId).trim();
                            if (!nickname && userDetail.nickname) nickname = String(userDetail.nickname).trim();
                            if (!uid && userDetail.id) uid = String(userDetail.id).trim();
                            isLoggedIn = true;
                        }
                    }
                } catch (e) {}

                // B. Đọc window globals: SIGI_STATE
                try {
                    if (window.SIGI_STATE) {
                        const sigi = window.SIGI_STATE;
                        const appUser = sigi.AppContext?.appContext?.user;
                        if (appUser) {
                            if (!uniqueId && appUser.uniqueId) uniqueId = String(appUser.uniqueId).trim();
                            if (!nickname && appUser.nickname) nickname = String(appUser.nickname).trim();
                            if (!uid && appUser.uid) uid = String(appUser.uid).trim();
                            if (appUser.isLoggedIn) isLoggedIn = true;
                        }
                        if (sigi.UserModule?.currentId) {
                            const curUser = sigi.UserModule.users?.[sigi.UserModule.currentId];
                            if (curUser) {
                                if (!uniqueId && curUser.uniqueId) uniqueId = String(curUser.uniqueId).trim();
                                if (!nickname && curUser.nickname) nickname = String(curUser.nickname).trim();
                                if (!uid && curUser.id) uid = String(curUser.id).trim();
                                isLoggedIn = true;
                            }
                        }
                    }
                } catch (e) {}

                // C. Gọi endpoint xác thực nội bộ TikTok nếu chưa lấy được danh tính
                if (!uniqueId || !nickname) {
                    try {
                        const res = await fetch('/passport/web/account/info/?aid=1988', { credentials: 'include' });
                        if (res.ok) {
                            const json = await res.json();
                            if (json && json.data) {
                                const d = json.data;
                                if (d.user_id_str || d.user_id) uid = uid || String(d.user_id_str || d.user_id).trim();
                                if (d.username || d.unique_id) uniqueId = uniqueId || String(d.username || d.unique_id).trim();
                                if (d.screen_name || d.nickname) nickname = nickname || String(d.screen_name || d.nickname).trim();
                                isLoggedIn = true;
                            }
                        }
                    } catch (e) {}
                }

                // D. Đọc profile link và avatar từ DOM Header / Navigation
                try {
                    const profileLinks = Array.from(document.querySelectorAll('a[href*=""/@""]'));
                    for (const link of profileLinks) {
                        const href = link.getAttribute('href') || '';
                        const m = href.match(/@([a-zA-Z0-9_.-]+)/);
                        if (m && m[1]) {
                            const candidate = m[1].trim();
                            if (link.closest('header') ||
                                link.closest('[data-e2e=""profile-icon""]') ||
                                link.closest('[data-e2e=""nav-profile""]') ||
                                link.closest('nav') ||
                                link.querySelector('img[alt*=""avatar""], img[alt*=""profile""], [data-e2e=""user-avatar""]')) {
                                if (!uniqueId) uniqueId = candidate;
                                isLoggedIn = true;
                                break;
                            }
                        }
                    }

                    const avatarImg = document.querySelector('header img[alt], [data-e2e=""profile-icon""] img, [data-e2e=""nav-profile""] img');
                    if (avatarImg && avatarImg.getAttribute('alt')) {
                        const alt = avatarImg.getAttribute('alt').trim();
                        if (alt && !alt.toLowerCase().includes('avatar') && !alt.toLowerCase().includes('profile')) {
                            if (!nickname) nickname = alt;
                        }
                    }

                    const loginBtn = document.querySelector('[data-e2e=""top-login-button""]') ||
                                     Array.from(document.querySelectorAll('button')).find(b => {
                                         const t = (b.textContent || '').trim().toLowerCase();
                                         return t === 'log in' || t === 'đăng nhập';
                                     });
                    if (loginBtn) {
                        hasLoginButton = true;
                    }
                } catch (e) {}

                // E. Đọc LocalStorage
                try {
                    for (let i = 0; i < localStorage.length; i++) {
                        const k = localStorage.key(i);
                        if (!k) continue;
                        if (k.includes('user') || k.includes('passport') || k.includes('session')) {
                            const val = localStorage.getItem(k);
                            if (!val) continue;
                            try {
                                const parsed = JSON.parse(val);
                                if (parsed && typeof parsed === 'object') {
                                    if (parsed.uniqueId && !uniqueId) uniqueId = String(parsed.uniqueId).trim();
                                    if (parsed.nickname && !nickname) nickname = String(parsed.nickname).trim();
                                    if (parsed.uid && !uid) uid = String(parsed.uid).trim();
                                }
                            } catch (e) {}
                        }
                    }
                } catch (e) {}

                return JSON.stringify({ uniqueId, nickname, uid, secUid, isLoggedIn, hasLoginButton });
            }");

            string? parsedUniqueId = null;
            string? parsedNickname = null;
            string? parsedUid = null;
            bool parsedIsLoggedIn = false;
            bool parsedHasLoginButton = false;

            if (!string.IsNullOrWhiteSpace(jsIdentity))
            {
                using var doc = JsonDocument.Parse(jsIdentity);
                if (doc.RootElement.TryGetProperty("uniqueId", out var uProp) && uProp.ValueKind == JsonValueKind.String)
                    parsedUniqueId = uProp.GetString()?.Trim();
                if (doc.RootElement.TryGetProperty("nickname", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                    parsedNickname = nProp.GetString()?.Trim();
                if (doc.RootElement.TryGetProperty("uid", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    parsedUid = idProp.GetString()?.Trim();
                if (doc.RootElement.TryGetProperty("isLoggedIn", out var lProp) && (lProp.ValueKind == JsonValueKind.True || lProp.ValueKind == JsonValueKind.False))
                    parsedIsLoggedIn = lProp.GetBoolean();
                if (doc.RootElement.TryGetProperty("hasLoginButton", out var btnProp) && (btnProp.ValueKind == JsonValueKind.True || btnProp.ValueKind == JsonValueKind.False))
                    parsedHasLoginButton = btnProp.GetBoolean();
            }

            // Nếu không có cookie phiên đăng nhập và không có dấu hiệu đã đăng nhập từ trang:
            if (!hasSessionCookie && !parsedIsLoggedIn && string.IsNullOrWhiteSpace(parsedUniqueId))
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profile.ProfileId,
                    sessionStatus: BrowserSessionStatus.Unverified,
                    reasonCode: BrowserReasonCode.LoginRequired,
                    cancellationToken: cancellationToken);

                return new ProfileIdentityResolutionResult(
                    IsSuccess: false,
                    ProfileId: profile.ProfileId,
                    Provider: "tiktok",
                    AccountIdentity: null,
                    SessionStatus: BrowserSessionStatus.Unverified,
                    ErrorMessage: "Không tìm thấy cookie phiên đăng nhập hợp lệ (sessionid) trên tiktok.com. Vui lòng đăng nhập qua trình duyệt.");
            }

            string? finalUid = parsedUid ?? uidCookie;
            string? handle = !string.IsNullOrWhiteSpace(parsedUniqueId)
                ? (parsedUniqueId.StartsWith('@') ? parsedUniqueId : $"@{parsedUniqueId}")
                : null;
            string? nickname = !string.IsNullOrWhiteSpace(parsedNickname) ? parsedNickname : null;

            // Định dạng danh tính: @uniqueId (Nickname), hoặc @uniqueId (UID), hoặc @uniqueId, v.v.
            string? finalIdentity = null;
            if (!string.IsNullOrWhiteSpace(handle))
            {
                if (!string.IsNullOrWhiteSpace(nickname) && !string.Equals(nickname, handle.TrimStart('@'), StringComparison.OrdinalIgnoreCase))
                {
                    finalIdentity = $"{handle} ({nickname})";
                }
                else if (!string.IsNullOrWhiteSpace(finalUid))
                {
                    finalIdentity = $"{handle} ({finalUid})";
                }
                else
                {
                    finalIdentity = handle;
                }
            }
            else if (!string.IsNullOrWhiteSpace(nickname))
            {
                if (!string.IsNullOrWhiteSpace(finalUid))
                {
                    finalIdentity = $"{nickname} ({finalUid})";
                }
                else
                {
                    finalIdentity = nickname;
                }
            }
            else if (!string.IsNullOrWhiteSpace(finalUid))
            {
                finalIdentity = $"TikTok ({finalUid})";
            }
            else if (!string.IsNullOrWhiteSpace(profile.AccountIdentity))
            {
                finalIdentity = profile.AccountIdentity;
            }

            if (string.IsNullOrWhiteSpace(finalIdentity))
            {
                return new ProfileIdentityResolutionResult(
                    IsSuccess: false,
                    ProfileId: profile.ProfileId,
                    Provider: "tiktok",
                    AccountIdentity: null,
                    SessionStatus: BrowserSessionStatus.Unverified,
                    ErrorMessage: "Phiên làm việc TikTok đã mở nhưng chưa lấy được danh tính tài khoản hợp lệ. Vui lòng đảm bảo bạn đã hoàn tất đăng nhập trên cửa sổ trình duyệt.");
            }

            // 4. Lưu trạng thái thành công bền vững vào ProfileRegistry
            await _profileRegistry.UpdateProfileStateAsync(
                profile.ProfileId,
                sessionStatus: BrowserSessionStatus.Connected,
                accountIdentity: finalIdentity,
                cancellationToken: cancellationToken);

            return ProfileIdentityResolutionResult.Success(
                profileId: profile.ProfileId,
                provider: "tiktok",
                accountIdentity: finalIdentity,
                sessionStatus: BrowserSessionStatus.Connected);
        }
        catch (TimeoutException tex)
        {
            return new ProfileIdentityResolutionResult(
                IsSuccess: false,
                ProfileId: profile.ProfileId,
                Provider: "tiktok",
                AccountIdentity: null,
                SessionStatus: BrowserSessionStatus.Unverified,
                ErrorMessage: $"Quá thời gian tải trang TikTok (Timeout): {tex.Message}. Vui lòng kiểm tra kết nối mạng và thử lại.");
        }
        catch (PlaywrightException pex) when (pex.Message.Contains("net::") || pex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return new ProfileIdentityResolutionResult(
                IsSuccess: false,
                ProfileId: profile.ProfileId,
                Provider: "tiktok",
                AccountIdentity: null,
                SessionStatus: BrowserSessionStatus.Unverified,
                ErrorMessage: $"Lỗi kết nối mạng khi tải trang TikTok: {pex.Message}. Vui lòng kiểm tra kết nối mạng và thử lại.");
        }
        catch (Exception ex)
        {
            return new ProfileIdentityResolutionResult(
                IsSuccess: false,
                ProfileId: profile.ProfileId,
                Provider: "tiktok",
                AccountIdentity: null,
                SessionStatus: BrowserSessionStatus.Unverified,
                ErrorMessage: $"Lỗi kiểm tra phiên TikTok: {ex.Message}");
        }
    }
}
