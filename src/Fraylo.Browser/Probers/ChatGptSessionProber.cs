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
/// Thực hiện thẩm định và trích xuất danh tính phiên làm việc cho nền tảng ChatGPT Web (chatgpt.com).
/// </summary>
public sealed class ChatGptSessionProber : IPlatformSessionProber
{
    private readonly IProfileRegistry _profileRegistry;

    public ChatGptSessionProber(IProfileRegistry profileRegistry)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
    }

    public string DefaultUrl => ProviderUrlResolver.DefaultChatGptUrl;

    public bool SupportsPlatform(string platformOrProviderKey)
    {
        if (string.IsNullOrWhiteSpace(platformOrProviderKey)) return false;
        var normalized = platformOrProviderKey.Trim().ToLowerInvariant();
        return normalized is "chatgpt-web" or "chatgpt" or "openai";
    }

    public async Task<ProfileIdentityResolutionResult> ProbeAsync(
        BrowserProfileInfo profile,
        IBrowserProcessController processController,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (processController == null)
        {
            return ProfileIdentityResolutionResult.Failed("Chưa khởi tạo IBrowserProcessController để kiểm tra phiên ChatGPT.");
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
                    return await ExecuteChatGptProbeOnPageAsync(page, activeHandle.Context, profile, cancellationToken);
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

            return await ExecuteChatGptProbeOnPageAsync(page, tempHandle.Context, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException tex)
        {
            return ProfileIdentityResolutionResult.Failed($"Quá thời gian tải trang ChatGPT (Timeout): {tex.Message}. Vui lòng kiểm tra mạng và thử lại.");
        }
        catch (PlaywrightException pex) when (pex.Message.Contains("net::") || pex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileIdentityResolutionResult.Failed($"Lỗi kết nối mạng khi tải trang ChatGPT: {pex.Message}. Vui lòng kiểm tra mạng và thử lại.");
        }
        catch (Exception ex)
        {
            return ProfileIdentityResolutionResult.Failed($"Lỗi khởi chạy kiểm tra phiên ChatGPT: {ex.Message}");
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

    private async Task<ProfileIdentityResolutionResult> ExecuteChatGptProbeOnPageAsync(
        IPage page,
        IBrowserContext context,
        BrowserProfileInfo profile,
        CancellationToken cancellationToken)
    {
        try
        {
            string currentUrl = page.Url;
            if (!currentUrl.StartsWith("https://chatgpt.com", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await page.GotoAsync("https://chatgpt.com", new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 15000
                    });
                }
                catch (TimeoutException) { }
                catch (PlaywrightException) { }
            }

            // Đợi trang hydrate và tải cookies/dữ liệu phiên
            await page.WaitForTimeoutAsync(2000);

            string finalUrl = page.Url;

            // 1. Kiểm tra nếu trang điều hướng về trang đăng nhập
            if (finalUrl.Contains("/auth/login", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("auth0.openai.com", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("auth.openai.com", StringComparison.OrdinalIgnoreCase))
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profile.ProfileId,
                    sessionStatus: BrowserSessionStatus.Unverified,
                    reasonCode: BrowserReasonCode.LoginRequired,
                    cancellationToken: cancellationToken);

                return new ProfileIdentityResolutionResult(
                    IsSuccess: false,
                    ProfileId: profile.ProfileId,
                    Provider: "chatgpt-web",
                    AccountIdentity: null,
                    SessionStatus: BrowserSessionStatus.Unverified,
                    ErrorMessage: "Tài khoản ChatGPT chưa đăng nhập hoặc phiên đã hết hạn. Vui lòng bấm 'Đăng nhập trình duyệt' để đăng nhập.");
            }

            // 2. Kiểm tra cookies phiên 'next-auth.session-token' hoặc '_puid'
            var cookies = await context.CookiesAsync("https://chatgpt.com");
            bool hasSessionToken = cookies.Any(c =>
                c.Name.StartsWith("__Secure-next-auth.session-token", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.Value));

            string? puidUserId = null;
            var puidCookie = cookies.FirstOrDefault(c => string.Equals(c.Name, "_puid", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(puidCookie))
            {
                puidUserId = puidCookie.Contains(':') ? puidCookie.Split(':')[0].Trim() : puidCookie.Trim();
            }

            if (!hasSessionToken && string.IsNullOrWhiteSpace(puidUserId))
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profile.ProfileId,
                    sessionStatus: BrowserSessionStatus.Unverified,
                    reasonCode: BrowserReasonCode.LoginRequired,
                    cancellationToken: cancellationToken);

                return new ProfileIdentityResolutionResult(
                    IsSuccess: false,
                    ProfileId: profile.ProfileId,
                    Provider: "chatgpt-web",
                    AccountIdentity: null,
                    SessionStatus: BrowserSessionStatus.Unverified,
                    ErrorMessage: "Không tìm thấy cookie phiên đăng nhập hợp lệ trên chatgpt.com. Vui lòng đăng nhập qua trình duyệt.");
            }

            // 3. Trích xuất danh tính (User ID, Display Name, Email, Plan) từ DOM, NextAuth Session, IndexedDB và LocalStorage
            var jsIdentity = await page.EvaluateAsync<string>(@"async () => {
                let name = null;
                let email = null;
                let plan = null;
                let userId = null;
                let accountId = null;

                // A. Thử NextAuth session endpoint
                try {
                    const res = await fetch('/api/auth/session');
                    if (res.ok) {
                        const data = await res.json();
                        if (data && data.user) {
                            if (data.user.name) name = String(data.user.name).trim();
                            if (data.user.email) email = String(data.user.email).trim();
                            if (data.user.id) userId = String(data.user.id).trim();
                        }
                    }
                } catch (e) {}

                // B. Thử đọc profile button trên DOM
                try {
                    const profileBtn = document.querySelector('button[data-testid=""profile-button""]') ||
                                       document.querySelector('[data-testid=""profile-button""]') ||
                                       document.querySelector('div[data-testid=""user-menu""]');
                    if (profileBtn) {
                        const lines = (profileBtn.innerText || '')
                            .split('\n')
                            .map(s => s.trim())
                            .filter(Boolean);
                        for (const l of lines) {
                            if (['Plus', 'Pro', 'Team', 'Free', 'Enterprise'].includes(l)) {
                                plan = l;
                            } else if (l.length > 1 && !name && !['DM', 'ChatGPT', 'New chat'].includes(l)) {
                                name = l;
                            }
                        }
                    }
                } catch (e) {}

                // C. Thử đọc IndexedDB: codex-browser-host -> records -> local-document-identity
                try {
                    await new Promise((resolve) => {
                        const req = indexedDB.open('codex-browser-host');
                        req.onsuccess = () => {
                            const db = req.result;
                            if (!db.objectStoreNames.contains('records')) { db.close(); return resolve(); }
                            const tx = db.transaction('records', 'readonly');
                            const st = tx.objectStore('records');
                            const getReq = st.getAll();
                            getReq.onsuccess = () => {
                                db.close();
                                const records = getReq.result || [];
                                for (const r of records) {
                                    if (r && r['local-document-identity']) {
                                        const ldi = r['local-document-identity'];
                                        if (ldi.userId && !userId) userId = String(ldi.userId).trim();
                                        if (ldi.accountId && !accountId) accountId = String(ldi.accountId).trim();
                                    }
                                }
                                resolve();
                            };
                            getReq.onerror = () => { db.close(); resolve(); };
                        };
                        req.onerror = () => resolve();
                    });
                } catch (e) {}

                // D. Thử đọc LocalStorage
                try {
                    if (!userId) {
                        const pli = localStorage.getItem('precise_location_identity');
                        if (pli) {
                            const parsed = JSON.parse(pli);
                            if (Array.isArray(parsed) && parsed.length >= 2 && parsed[1] && parsed[1].startsWith('user-')) {
                                userId = parsed[1];
                            }
                        }
                    }
                    if (!email) {
                        for (let i = 0; i < localStorage.length; i++) {
                            const k = localStorage.key(i);
                            if (k && k.includes('composerDraft')) {
                                const v = localStorage.getItem(k);
                                if (v && v.includes('@') && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v.trim())) {
                                    email = v.trim();
                                    break;
                                }
                            }
                        }
                    }
                } catch (e) {}

                return JSON.stringify({ name, email, plan, userId, accountId });
            }");

            string? parsedName = null;
            string? parsedEmail = null;
            string? parsedPlan = null;
            string? parsedUserId = null;

            if (!string.IsNullOrWhiteSpace(jsIdentity))
            {
                using var doc = JsonDocument.Parse(jsIdentity);
                if (doc.RootElement.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                    parsedName = nProp.GetString()?.Trim();
                if (doc.RootElement.TryGetProperty("email", out var eProp) && eProp.ValueKind == JsonValueKind.String)
                    parsedEmail = eProp.GetString()?.Trim();
                if (doc.RootElement.TryGetProperty("plan", out var plProp) && plProp.ValueKind == JsonValueKind.String)
                    parsedPlan = plProp.GetString()?.Trim();
                if (doc.RootElement.TryGetProperty("userId", out var uProp) && uProp.ValueKind == JsonValueKind.String)
                    parsedUserId = uProp.GetString()?.Trim();
            }

            string? finalUserId = parsedUserId ?? puidUserId;
            string? displayName = parsedName;
            if (string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(parsedEmail))
            {
                displayName = parsedEmail;
            }

            // Fallback giữ lại tên hiển thị đã biết trước đó nếu có
            if (string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(profile.AccountIdentity))
            {
                displayName = profile.AccountIdentity;
                if (displayName.Contains('(') && displayName.EndsWith(')'))
                {
                    displayName = displayName.Substring(0, displayName.LastIndexOf('(')).Trim();
                }
            }

            string? finalIdentity = null;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                if (!string.IsNullOrWhiteSpace(finalUserId))
                {
                    finalIdentity = $"{displayName} ({finalUserId})";
                }
                else
                {
                    finalIdentity = displayName;
                }
            }
            else if (!string.IsNullOrWhiteSpace(finalUserId))
            {
                finalIdentity = $"ChatGPT ({finalUserId})";
            }

            if (string.IsNullOrWhiteSpace(finalIdentity))
            {
                return new ProfileIdentityResolutionResult(
                    IsSuccess: false,
                    ProfileId: profile.ProfileId,
                    Provider: "chatgpt-web",
                    AccountIdentity: null,
                    SessionStatus: BrowserSessionStatus.Unverified,
                    ErrorMessage: "Phiên làm việc ChatGPT đã mở nhưng chưa lấy được danh tính tài khoản hợp lệ. Vui lòng đảm bảo bạn đã hoàn tất đăng nhập trên cửa sổ trình duyệt.");
            }

            // 4. Lưu trạng thái thành công bền vững vào ProfileRegistry
            await _profileRegistry.UpdateProfileStateAsync(
                profile.ProfileId,
                sessionStatus: BrowserSessionStatus.Connected,
                accountIdentity: finalIdentity,
                cancellationToken: cancellationToken);

            return ProfileIdentityResolutionResult.Success(
                profileId: profile.ProfileId,
                provider: "chatgpt-web",
                accountIdentity: finalIdentity,
                sessionStatus: BrowserSessionStatus.Connected);
        }
        catch (TimeoutException tex)
        {
            return new ProfileIdentityResolutionResult(
                IsSuccess: false,
                ProfileId: profile.ProfileId,
                Provider: "chatgpt-web",
                AccountIdentity: null,
                SessionStatus: BrowserSessionStatus.Unverified,
                ErrorMessage: $"Quá thời gian tải trang ChatGPT (Timeout): {tex.Message}. Vui lòng kiểm tra kết nối mạng và thử lại.");
        }
        catch (PlaywrightException pex) when (pex.Message.Contains("net::") || pex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return new ProfileIdentityResolutionResult(
                IsSuccess: false,
                ProfileId: profile.ProfileId,
                Provider: "chatgpt-web",
                AccountIdentity: null,
                SessionStatus: BrowserSessionStatus.Unverified,
                ErrorMessage: $"Lỗi kết nối mạng khi tải trang ChatGPT: {pex.Message}. Vui lòng kiểm tra kết nối mạng và thử lại.");
        }
        catch (Exception ex)
        {
            return new ProfileIdentityResolutionResult(
                IsSuccess: false,
                ProfileId: profile.ProfileId,
                Provider: "chatgpt-web",
                AccountIdentity: null,
                SessionStatus: BrowserSessionStatus.Unverified,
                ErrorMessage: $"Lỗi kiểm tra phiên ChatGPT: {ex.Message}");
        }
    }
}
