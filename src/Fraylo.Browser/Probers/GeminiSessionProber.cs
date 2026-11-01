using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Accounts;
using Fraylo.Contracts.Browser;

namespace Fraylo.Browser.Probers;

/// <summary>
/// Thực hiện thẩm định và trích xuất danh tính phiên làm việc cho nền tảng Google Gemini Web (gemini.google.com).
/// </summary>
public sealed class GeminiSessionProber : IPlatformSessionProber
{
    private readonly IProfileRegistry _profileRegistry;

    public GeminiSessionProber(IProfileRegistry profileRegistry)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
    }

    public string DefaultUrl => ProviderUrlResolver.DefaultGeminiUrl;

    public bool SupportsPlatform(string platformOrProviderKey)
    {
        if (string.IsNullOrWhiteSpace(platformOrProviderKey)) return false;
        var normalized = platformOrProviderKey.Trim().ToLowerInvariant();
        return normalized is "gemini-web" or "gemini" or "google";
    }

    public async Task<ProfileIdentityResolutionResult> ProbeAsync(
        BrowserProfileInfo profile,
        IBrowserProcessController processController,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (processController == null)
        {
            return ProfileIdentityResolutionResult.Failed("Chưa khởi tạo IBrowserProcessController để kiểm tra phiên Gemini.");
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
                    return await ExecuteGeminiProbeOnPageAsync(page, profile, cancellationToken);
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

            return await ExecuteGeminiProbeOnPageAsync(page, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException tex)
        {
            return ProfileIdentityResolutionResult.Failed($"Quá thời gian tải trang Gemini (Timeout): {tex.Message}. Vui lòng kiểm tra mạng và thử lại.");
        }
        catch (PlaywrightException pex) when (pex.Message.Contains("net::") || pex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileIdentityResolutionResult.Failed($"Lỗi kết nối mạng khi tải trang Gemini: {pex.Message}. Vui lòng kiểm tra mạng và thử lại.");
        }
        catch (Exception ex)
        {
            return ProfileIdentityResolutionResult.Failed($"Lỗi khởi chạy kiểm tra phiên Gemini: {ex.Message}");
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

    private async Task<ProfileIdentityResolutionResult> ExecuteGeminiProbeOnPageAsync(
        IPage page,
        BrowserProfileInfo profile,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!page.Url.StartsWith(ProviderUrlResolver.DefaultGeminiUrl, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await page.GotoAsync(ProviderUrlResolver.DefaultGeminiUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 15000
                    });
                }
                catch (TimeoutException) { }
                catch (PlaywrightException) { }
            }

            // Chưa đăng nhập: Google chuyển hướng sang accounts.google.com
            if (page.Url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profile.ProfileId,
                    sessionStatus: BrowserSessionStatus.Unverified,
                    reasonCode: BrowserReasonCode.LoginRequired,
                    cancellationToken: cancellationToken);

                return ProfileIdentityResolutionResult.Failed("Tài khoản Gemini chưa đăng nhập hoặc phiên đã hết hạn. Vui lòng bấm 'Đăng nhập trình duyệt' để đăng nhập.");
            }

            // Nút tài khoản Google trên Gemini có aria-label chứa email; chờ trang render xong
            try
            {
                await page.WaitForSelectorAsync("[aria-label*='@']", new PageWaitForSelectorOptions { Timeout = 10000 });
            }
            catch (TimeoutException) { }
            catch (PlaywrightException) { }

            string? email = await page.EvaluateAsync<string?>(@"() => {
                for (const el of document.querySelectorAll('[aria-label*=""@""]')) {
                    const m = (el.getAttribute('aria-label') || '').match(/[^\s():]+@[^\s()]+/);
                    if (m) return m[0];
                }
                return null;
            }");

            if (string.IsNullOrWhiteSpace(email))
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profile.ProfileId,
                    sessionStatus: BrowserSessionStatus.Unverified,
                    reasonCode: BrowserReasonCode.LoginRequired,
                    cancellationToken: cancellationToken);
                return ProfileIdentityResolutionResult.Failed("Phiên Gemini đã mở nhưng chưa đọc được email tài khoản. Vui lòng hoàn tất đăng nhập trên cửa sổ trình duyệt rồi kiểm tra lại.");
            }

            await _profileRegistry.UpdateProfileStateAsync(
                profile.ProfileId,
                sessionStatus: BrowserSessionStatus.Connected,
                accountIdentity: email,
                cancellationToken: cancellationToken);

            return ProfileIdentityResolutionResult.Success(
                profileId: profile.ProfileId,
                provider: "gemini-web",
                accountIdentity: email,
                sessionStatus: BrowserSessionStatus.Connected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProfileIdentityResolutionResult.Failed($"Lỗi kiểm tra phiên Gemini: {ex.Message}");
        }
    }
}
