using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Accounts;

namespace Fraylo.Browser.Probers;

/// <summary>
/// Xác minh phiên Facebook của một profile. Không đọc DOM: phiên hợp lệ khi có cookie phiên
/// (`c_user` = ID tài khoản, `xs`) và trang không bị chuyển sang đăng nhập/checkpoint.
/// shortcut: danh tính là ID số từ cookie, chưa có tên hiển thị; bổ sung theo bằng chứng Giai đoạn A của FACEBOOK_PUBLISHING_PLAN.
/// </summary>
public sealed class FacebookPlatformProber : IPlatformSessionProber
{
    private readonly IProfileRegistry _profileRegistry;

    public FacebookPlatformProber(IProfileRegistry profileRegistry)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
    }

    public string DefaultUrl => ProviderUrlResolver.DefaultFacebookUrl;

    public bool SupportsPlatform(string platformOrProviderKey) =>
        string.Equals(platformOrProviderKey?.Trim(), "facebook", StringComparison.OrdinalIgnoreCase);

    /// <summary>Danh tính "Facebook (ID)" khi có đủ cookie phiên, ngược lại null.</summary>
    public static string? ReadIdentity(IEnumerable<(string Name, string Value)> cookies)
    {
        var list = cookies.ToList();
        string? Value(string name) => list.FirstOrDefault(c => c.Name == name).Value is { Length: > 0 } v ? v.Trim() : null;
        var userId = Value("c_user");
        return userId is { Length: > 0 } && userId.All(char.IsAsciiDigit) && Value("xs") != null
            ? $"Facebook ({userId})"
            : null;
    }

    /// <summary>Trang đăng nhập hoặc checkpoint nghĩa là phiên chưa dùng được.</summary>
    public static bool IsLoginOrCheckpointUrl(string url) =>
        url.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/checkpoint", StringComparison.OrdinalIgnoreCase);

    public async Task<ProfileIdentityResolutionResult> ProbeAsync(
        BrowserProfileInfo profile,
        IBrowserProcessController processController,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(processController);

        var handle = processController.TryGetContext(profile.ProfileId);
        var ownsContext = handle == null;
        IPage? page = null;
        try
        {
            handle ??= await processController.LaunchPersistentContextAsync(
                profile.ProfileId, isHeadless: true, cancellationToken: cancellationToken);
            page = await handle.Context.NewPageAsync();
            await page.GotoAsync(DefaultUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

            if (IsLoginOrCheckpointUrl(page.Url))
                return await FailAsync(profile, "Facebook yêu cầu đăng nhập hoặc xác minh (checkpoint). Hãy đăng nhập thủ công trong Cài đặt.", cancellationToken);

            var cookies = await handle.Context.CookiesAsync(new[] { DefaultUrl });
            var identity = ReadIdentity(cookies.Select(c => (c.Name, c.Value)));
            if (identity == null)
                return await FailAsync(profile, "Không thấy phiên đăng nhập Facebook hợp lệ. Hãy đăng nhập thủ công trong Cài đặt.", cancellationToken);

            await _profileRegistry.UpdateProfileStateAsync(
                profile.ProfileId, sessionStatus: BrowserSessionStatus.Connected, accountIdentity: identity,
                cancellationToken: cancellationToken);
            return ProfileIdentityResolutionResult.Success(
                profileId: profile.ProfileId, provider: "facebook", accountIdentity: identity,
                sessionStatus: BrowserSessionStatus.Connected);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            return ProfileIdentityResolutionResult.Failed($"Không kiểm tra được phiên Facebook: {ex.Message}");
        }
        finally
        {
            if (page != null && !page.IsClosed) { try { await page.CloseAsync(); } catch { } }
            if (ownsContext) { try { await processController.CloseContextAsync(profile.ProfileId, CancellationToken.None); } catch { } }
        }
    }

    private async Task<ProfileIdentityResolutionResult> FailAsync(BrowserProfileInfo profile, string message, CancellationToken ct)
    {
        await _profileRegistry.UpdateProfileStateAsync(
            profile.ProfileId, sessionStatus: BrowserSessionStatus.Unverified,
            reasonCode: BrowserReasonCode.LoginRequired, cancellationToken: ct);
        return new ProfileIdentityResolutionResult(
            IsSuccess: false, ProfileId: profile.ProfileId, Provider: "facebook", AccountIdentity: null,
            SessionStatus: BrowserSessionStatus.Unverified, ErrorMessage: message);
    }
}
