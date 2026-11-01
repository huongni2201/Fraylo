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
/// Thực hiện thẩm định và trích xuất danh tính phiên làm việc cho nền tảng MuseAI (muse.ai).
/// </summary>
public sealed class MuseAiSessionProber : IPlatformSessionProber
{
    private readonly IProfileRegistry _profileRegistry;

    public MuseAiSessionProber(IProfileRegistry profileRegistry)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
    }

    public string DefaultUrl => ProviderUrlResolver.DefaultMuseAiUrl;

    public bool SupportsPlatform(string platformOrProviderKey)
    {
        if (string.IsNullOrWhiteSpace(platformOrProviderKey)) return false;
        var normalized = platformOrProviderKey.Trim().ToLowerInvariant();
        return normalized is "museai" or "museai-video" or "muse";
    }

    public async Task<ProfileIdentityResolutionResult> ProbeAsync(
        BrowserProfileInfo profile,
        IBrowserProcessController processController,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (processController == null)
        {
            return ProfileIdentityResolutionResult.Failed("Chưa khởi tạo IBrowserProcessController để kiểm tra phiên MuseAI.");
        }

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
                        page = activeHandle.Context.Pages.FirstOrDefault(p => !p.IsClosed && p.Url.Contains("muse.ai"))
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
                    return await ExecuteMuseAiProbeOnPageAsync(page, activeHandle.Context, profile, cancellationToken);
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
                initialUrl: "https://muse.ai",
                cancellationToken: cancellationToken);

            IPage? page = null;
            if (tempHandle.Context.Pages != null)
            {
                page = tempHandle.Context.Pages.FirstOrDefault(p => !p.IsClosed);
            }
            page ??= await tempHandle.Context.NewPageAsync();

            return await ExecuteMuseAiProbeOnPageAsync(page, tempHandle.Context, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException tex)
        {
            return ProfileIdentityResolutionResult.Failed($"Quá thời gian tải trang MuseAI (Timeout): {tex.Message}. Vui lòng kiểm tra mạng và thử lại.");
        }
        catch (PlaywrightException pex) when (pex.Message.Contains("net::") || pex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileIdentityResolutionResult.Failed($"Lỗi kết nối mạng khi tải trang MuseAI: {pex.Message}. Vui lòng kiểm tra mạng và thử lại.");
        }
        catch (Exception ex)
        {
            return ProfileIdentityResolutionResult.Failed($"Lỗi khởi chạy kiểm tra phiên MuseAI: {ex.Message}");
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

    private async Task<ProfileIdentityResolutionResult> ExecuteMuseAiProbeOnPageAsync(
        IPage page,
        IBrowserContext context,
        BrowserProfileInfo profile,
        CancellationToken cancellationToken)
    {
        try
        {
            string currentUrl = page.Url;
            if (!currentUrl.StartsWith("https://muse.ai", StringComparison.OrdinalIgnoreCase))
            {
                await page.GotoAsync("https://muse.ai", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 15000
                });
            }

            // Đợi trang hydrate và tải dữ liệu phiên
            await page.WaitForTimeoutAsync(2500);

            string finalUrl = page.Url;

            // 1. Kiểm tra nếu trang điều hướng về trang đăng nhập
            if (finalUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("/auth", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("accounts.meta.com", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("auth.meta.com", StringComparison.OrdinalIgnoreCase))
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profile.ProfileId,
                    sessionStatus: BrowserSessionStatus.Unverified,
                    reasonCode: BrowserReasonCode.LoginRequired,
                    cancellationToken: cancellationToken);

                return new ProfileIdentityResolutionResult(
                    IsSuccess: false,
                    ProfileId: profile.ProfileId,
                    Provider: "museai",
                    AccountIdentity: null,
                    SessionStatus: BrowserSessionStatus.Unverified,
                    ErrorMessage: "Tài khoản MuseAI chưa đăng nhập hoặc phiên đã hết hạn. Vui lòng bấm 'Đăng nhập trình duyệt' để đăng nhập.");
            }

            // 2. Kiểm tra cookies phiên 'hatch_sess'
            var cookies = await context.CookiesAsync("https://muse.ai");
            var sessCookie = cookies.FirstOrDefault(c => string.Equals(c.Name, "hatch_sess", StringComparison.OrdinalIgnoreCase));
            if (sessCookie == null)
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profile.ProfileId,
                    sessionStatus: BrowserSessionStatus.Unverified,
                    reasonCode: BrowserReasonCode.LoginRequired,
                    cancellationToken: cancellationToken);

                return new ProfileIdentityResolutionResult(
                    IsSuccess: false,
                    ProfileId: profile.ProfileId,
                    Provider: "museai",
                    AccountIdentity: null,
                    SessionStatus: BrowserSessionStatus.Unverified,
                    ErrorMessage: "Không tìm thấy cookie phiên 'hatch_sess' hợp lệ trên muse.ai. Vui lòng đăng nhập qua trình duyệt.");
            }

            // 3. Trích xuất danh tính (Meta Account ID / User Greeting Name) từ IndexedDB và DOM
            var idbData = await page.EvaluateAsync<string>(@"async () => {
                let ownerId = null;
                let scopeId = null;
                let userName = null;

                function getStoreRecords(dbName, storeName) {
                    return new Promise((resolve) => {
                        let req = indexedDB.open(dbName);
                        req.onsuccess = () => {
                            let db = req.result;
                            if (!db.objectStoreNames.contains(storeName)) { db.close(); return resolve([]); }
                            let tx = db.transaction(storeName, 'readonly');
                            let st = tx.objectStore(storeName);
                            let getReq = st.getAll(null, 5);
                            getReq.onsuccess = () => { db.close(); resolve(getReq.result || []); };
                            getReq.onerror = () => { db.close(); resolve([]); };
                        };
                        req.onerror = () => resolve([]);
                    });
                }

                let replacementRecords = await getStoreRecords('hatch-cvm-replacement', 'revokedSessions');
                if (replacementRecords && replacementRecords.length > 0 && replacementRecords[0].ownerId) {
                    ownerId = String(replacementRecords[0].ownerId).trim();
                }

                if (!ownerId) {
                    let provRecords = await getStoreRecords('hatch-cvm-provisioning', 'revokedSessions');
                    if (provRecords && provRecords.length > 0 && provRecords[0].ownerId) {
                        ownerId = String(provRecords[0].ownerId).trim();
                    }
                }

                function getStoreKeys(dbName, storeName) {
                    return new Promise((resolve) => {
                        let req = indexedDB.open(dbName);
                        req.onsuccess = () => {
                            let db = req.result;
                            if (!db.objectStoreNames.contains(storeName)) { db.close(); return resolve([]); }
                            let tx = db.transaction(storeName, 'readonly');
                            let st = tx.objectStore(storeName);
                            let getReq = st.getAllKeys(null, 5);
                            getReq.onsuccess = () => { db.close(); resolve(getReq.result || []); };
                            getReq.onerror = () => { db.close(); resolve([]); };
                        };
                        req.onerror = () => resolve([]);
                    });
                }

                let chatKeys = await getStoreKeys('hatch-chat-cache', 'main-transcripts');
                if (chatKeys && chatKeys.length > 0) {
                    let keyStr = String(chatKeys[0]);
                    let parts = keyStr.split('\0');
                    if (parts[0] && /^\d+$/.test(parts[0])) {
                        scopeId = parts[0];
                    }
                }

                if (!scopeId) {
                    let avatarKeys = await getStoreKeys('hatch-avatar-cache-v2', 'manifests');
                    if (avatarKeys && avatarKeys.length > 0) {
                        let keyStr = String(avatarKeys[0]);
                        let parts = keyStr.split('\0');
                        if (parts[0] && /^\d+$/.test(parts[0])) {
                            scopeId = parts[0];
                        }
                    }
                }

                let allText = document.body ? document.body.innerText : '';
                let m = allText.match(/Hey\s+([A-Za-z0-9_\-\.]+),\s+I(?:’|')m\s+your\s+personal\s+agent/i);
                if (m && m[1]) {
                    userName = m[1].trim();
                }

                if (!userName) {
                    let transcriptRecords = await getStoreRecords('hatch-chat-cache', 'main-transcripts');
                    for (let rec of transcriptRecords) {
                        try {
                            let str = JSON.stringify(rec);
                            let m2 = str.match(/Hey\s+([A-Za-z0-9_\-\.]+),\s+I(?:’|')m\s+your\s+personal\s+agent/i);
                            if (m2 && m2[1]) {
                                userName = m2[1].trim();
                                break;
                            }
                        } catch (e) {}
                    }
                }

                return JSON.stringify({ ownerId, scopeId, userName });
            }");

            string? parsedOwnerId = null;
            string? parsedScopeId = null;
            string? parsedUserName = null;

            if (!string.IsNullOrWhiteSpace(idbData))
            {
                using var doc = JsonDocument.Parse(idbData);
                if (doc.RootElement.TryGetProperty("ownerId", out var oProp) && oProp.ValueKind == JsonValueKind.String)
                    parsedOwnerId = oProp.GetString();
                if (doc.RootElement.TryGetProperty("scopeId", out var sProp) && sProp.ValueKind == JsonValueKind.String)
                    parsedScopeId = sProp.GetString();
                if (doc.RootElement.TryGetProperty("userName", out var uProp) && uProp.ValueKind == JsonValueKind.String)
                    parsedUserName = uProp.GetString();
            }

            string? resolvedId = parsedOwnerId ?? parsedScopeId;
            string? finalIdentity = null;

            if (!string.IsNullOrWhiteSpace(resolvedId))
            {
                if (!string.IsNullOrWhiteSpace(parsedUserName))
                {
                    finalIdentity = $"{parsedUserName} ({resolvedId})";
                }
                else
                {
                    finalIdentity = resolvedId;
                }
            }
            else if (!string.IsNullOrWhiteSpace(parsedUserName))
            {
                finalIdentity = parsedUserName;
            }

            if (string.IsNullOrWhiteSpace(finalIdentity))
            {
                return new ProfileIdentityResolutionResult(
                    IsSuccess: false,
                    ProfileId: profile.ProfileId,
                    Provider: "museai",
                    AccountIdentity: null,
                    SessionStatus: BrowserSessionStatus.Unverified,
                    ErrorMessage: "Phiên làm việc MuseAI đã mở nhưng chưa lấy được danh tính tài khoản hợp lệ (Meta Account ID / User Name). Vui lòng đảm bảo bạn đã hoàn tất đăng nhập trên cửa sổ trình duyệt.");
            }

            // 4. Lưu trạng thái thành công bền vững vào ProfileRegistry
            await _profileRegistry.UpdateProfileStateAsync(
                profile.ProfileId,
                sessionStatus: BrowserSessionStatus.Connected,
                accountIdentity: finalIdentity,
                cancellationToken: cancellationToken);

            return ProfileIdentityResolutionResult.Success(
                profileId: profile.ProfileId,
                provider: "museai",
                accountIdentity: finalIdentity,
                sessionStatus: BrowserSessionStatus.Connected);
        }
        catch (TimeoutException tex)
        {
            return new ProfileIdentityResolutionResult(
                IsSuccess: false,
                ProfileId: profile.ProfileId,
                Provider: "museai",
                AccountIdentity: null,
                SessionStatus: BrowserSessionStatus.Unverified,
                ErrorMessage: $"Quá thời gian tải trang MuseAI (Timeout): {tex.Message}. Vui lòng kiểm tra kết nối mạng và thử lại.");
        }
        catch (PlaywrightException pex) when (pex.Message.Contains("net::") || pex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return new ProfileIdentityResolutionResult(
                IsSuccess: false,
                ProfileId: profile.ProfileId,
                Provider: "museai",
                AccountIdentity: null,
                SessionStatus: BrowserSessionStatus.Unverified,
                ErrorMessage: $"Lỗi kết nối mạng khi tải trang MuseAI: {pex.Message}. Vui lòng kiểm tra kết nối mạng và thử lại.");
        }
        catch (Exception ex)
        {
            return new ProfileIdentityResolutionResult(
                IsSuccess: false,
                ProfileId: profile.ProfileId,
                Provider: "museai",
                AccountIdentity: null,
                SessionStatus: BrowserSessionStatus.Unverified,
                ErrorMessage: $"Lỗi kiểm tra phiên MuseAI: {ex.Message}");
        }
    }
}
