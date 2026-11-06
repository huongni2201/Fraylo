using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Browser.Probers;
using Fraylo.Contracts.Accounts;
using Fraylo.Contracts.Browser;

namespace Fraylo.Browser;

/// <summary>
/// Thẩm định trạng thái session cookies và giải quyết danh tính tài khoản theo hợp đồng IProfileIdentityResolver.
/// Hỗ trợ probe thực tế trên Chromium Persistent Context thông qua danh sách các IPlatformSessionProber chuyên biệt.
/// </summary>
public sealed class SessionProbe : ISessionProbe, IProfileIdentityResolver
{
    private readonly IProfileRegistry _profileRegistry;
    private readonly IBrowserProcessController? _processController;
    private readonly Func<string, string, CancellationToken, Task<ProfileIdentityResolutionResult?>>? _externalAdapter;
    private readonly Func<string, CancellationToken, Task<BrowserCheckSessionResponseData?>>? _sessionCheckAdapter;
    private readonly IReadOnlyList<IPlatformSessionProber> _probers;

    public SessionProbe(
        IProfileRegistry profileRegistry,
        IBrowserProcessController? processController = null,
        Func<string, string, CancellationToken, Task<ProfileIdentityResolutionResult?>>? externalAdapter = null,
        Func<string, CancellationToken, Task<BrowserCheckSessionResponseData?>>? sessionCheckAdapter = null,
        IReadOnlyList<IPlatformSessionProber>? probers = null)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
        _processController = processController;
        _externalAdapter = externalAdapter;
        _sessionCheckAdapter = sessionCheckAdapter;
        _probers = probers ?? new IPlatformSessionProber[]
        {
            new MuseAiSessionProber(_profileRegistry),
            new ChatGptSessionProber(_profileRegistry),
            new GeminiSessionProber(_profileRegistry),
            new TikTokPlatformProber(_profileRegistry),
            new FacebookPlatformProber(_profileRegistry)
        };
    }

    public async Task<BrowserCheckSessionResponseData> CheckSessionAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        if (_sessionCheckAdapter != null)
        {
            var customCheck = await _sessionCheckAdapter(profileId, cancellationToken);
            if (customCheck != null)
            {
                return customCheck;
            }
        }

        var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
        if (profile == null)
        {
            return new BrowserCheckSessionResponseData(
                SessionStatus: BrowserSessionStatus.Unverified,
                IdentityMatches: null);
        }

        // Nếu có IBrowserProcessController, thực hiện probe thực tế để có trạng thái phiên và danh tính mới nhất
        if (_processController != null)
        {
            string providerKey = profile.ProviderKey ?? profile.Kind;
            var probeResult = await ResolveProfileIdentityAsync(profileId, providerKey, cancellationToken);

            bool? probeIdentityMatches = probeResult.IsSuccess && !string.IsNullOrWhiteSpace(probeResult.AccountIdentity)
                ? true
                : (profile.AccountIdentity != null ? true : null);

            return new BrowserCheckSessionResponseData(
                SessionStatus: probeResult.SessionStatus ?? profile.SessionStatus,
                IdentityMatches: probeIdentityMatches);
        }

        bool? identityMatches = profile.AccountIdentity != null ? true : null;
        return new BrowserCheckSessionResponseData(
            SessionStatus: profile.SessionStatus,
            IdentityMatches: identityMatches);
    }

    public async Task<ProfileIdentityResolutionResult> ResolveProfileIdentityAsync(
        string profileId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
        if (profile == null)
        {
            return ProfileIdentityResolutionResult.Failed($"Hồ sơ trình duyệt '{profileId}' không tồn tại trong hệ thống.");
        }

        // 1. Nếu có adapter kiểm tra session bên ngoài (cho unit/smoke test custom)
        if (_externalAdapter != null)
        {
            var result = await _externalAdapter(profileId, provider, cancellationToken);
            if (result != null) return result;
        }

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        if (normalizedProvider == "museai-video") normalizedProvider = "museai";

        // 2. Thực hiện probe thực tế cho từng Provider nếu có IBrowserProcessController
        if (_processController != null)
        {
            // Cửa sổ đăng nhập thủ công đang giữ .Fraylo_lock: đóng để probe mở được profile
            if (_processController.IsLoginWindowOpen(profileId))
            {
                await _processController.CloseContextAsync(profileId, cancellationToken);
            }

            var prober = _probers.FirstOrDefault(p => p.SupportsPlatform(normalizedProvider));
            if (prober != null)
            {
                return await prober.ProbeAsync(profile, _processController, cancellationToken);
            }
        }

        // 3. Fallback: Nếu profile đã có trạng thái Connected và danh tính tài khoản hợp lệ từ trước
        if (string.Equals(profile.SessionStatus, BrowserSessionStatus.Connected, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(profile.AccountIdentity))
        {
            return ProfileIdentityResolutionResult.Success(
                profileId: profile.ProfileId,
                provider: provider,
                accountIdentity: profile.AccountIdentity,
                sessionStatus: BrowserSessionStatus.Connected);
        }

        // Khi chưa có adapter thật hoặc phiên chưa đăng nhập: Không fake PASS!
        return new ProfileIdentityResolutionResult(
            IsSuccess: false,
            ProfileId: profileId,
            Provider: provider,
            AccountIdentity: profile.AccountIdentity,
            SessionStatus: BrowserSessionStatus.Unverified,
            ErrorMessage: "Phiên làm việc chưa được xác thực (Unverified). Yêu cầu đăng nhập tài khoản trước khi liên kết.");
    }
}
