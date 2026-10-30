using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiToAI.Browser.Abstractions;
using TiToAI.Browser.Models;
using TiToAI.Contracts.Browser;

namespace TiToAI.Browser;

/// <summary>
/// Điều phối đăng nhập trình duyệt hiển thị (Headless = false) cho các profile độc lập.
/// Cho phép mở đồng thời nhiều cửa sổ đăng nhập theo thao tác chủ động của người dùng,
/// bảo đảm Single-Instance Lock cho từng userDataDir (không mở trùng khi bấm nhiều lần),
/// độc lập vòng đời giữa các profile và tuân thủ ngân sách tài nguyên max_open_browsers.
/// </summary>
public sealed class LoginCoordinator : ILoginCoordinator, IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _activeVisibleProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IProfileRegistry? _profileRegistry;
    private readonly IBrowserProcessController? _processController;
    private readonly ISessionProbe? _sessionProbe;
    private readonly Func<int>? _getMaxOpenBrowsers;
    private readonly Func<int>? _getOpenBrowserCount;
    private readonly Func<string, bool>? _canReplaceHeadlessContext;

    private bool _disposed;

    public string? CurrentVisibleProfileId
    {
        get
        {
            return _activeVisibleProfiles.Keys.FirstOrDefault();
        }
    }

    public IReadOnlyCollection<string> ActiveVisibleProfileIds => _activeVisibleProfiles.Keys.ToArray();

    public bool IsVisibleLoginActive => !_activeVisibleProfiles.IsEmpty;

    public bool IsProfileVisible(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return false;
        return _activeVisibleProfiles.ContainsKey(profileId);
    }

    public LoginCoordinator(
        IProfileRegistry? profileRegistry = null,
        IBrowserProcessController? processController = null,
        ISessionProbe? sessionProbe = null,
        Func<int>? getMaxOpenBrowsers = null,
        Func<int>? getOpenBrowserCount = null,
        Func<string, bool>? canReplaceHeadlessContext = null)
    {
        _profileRegistry = profileRegistry;
        _processController = processController;
        _sessionProbe = sessionProbe ?? (profileRegistry != null ? new SessionProbe(profileRegistry) : null);
        _getMaxOpenBrowsers = getMaxOpenBrowsers;
        _getOpenBrowserCount = getOpenBrowserCount;
        _canReplaceHeadlessContext = canReplaceHeadlessContext;

        if (_processController != null)
        {
            _processController.ContextClosed += OnProcessControllerContextClosed;
        }
    }

    public Task<BrowserRequestLoginResponseData> RequestLoginAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        return RequestLoginAsync(profileId, targetUrl: null, cancellationToken);
    }

    public async Task<BrowserRequestLoginResponseData> RequestLoginAsync(
        string profileId,
        string? targetUrl,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        // Khóa đồng bộ theo từng profile: đảm bảo bấm nhiều lần không mở trùng tiến trình
        var profileLock = _profileLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await profileLock.WaitAsync(cancellationToken);

        try
        {
            // Tự động giải quyết URL nếu caller chưa chỉ định
            if (string.IsNullOrWhiteSpace(targetUrl) && _profileRegistry != null)
            {
                var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
                if (profile != null)
                {
                    targetUrl = ProviderUrlResolver.ResolveUrl(profile.ProviderKey, profile.Kind);
                }
            }

            // Phòng thủ Stale State: nếu profileId được ghi nhận đang visible nhưng tiến trình controller thực tế không còn chạy
            if (_activeVisibleProfiles.ContainsKey(profileId) && _processController != null && !_processController.IsRunning(profileId))
            {
                _activeVisibleProfiles.TryRemove(profileId, out _);
            }

            // 1. Kiểm tra nếu cửa sổ trình duyệt của profile này ĐÃ ĐANG MỞ
            bool isAlreadyRunning = (_processController != null && _processController.IsRunning(profileId)) || _activeVisibleProfiles.ContainsKey(profileId);
            if (isAlreadyRunning && _processController?.TryGetContext(profileId) is { IsHeadless: true })
            {
                if (_canReplaceHeadlessContext?.Invoke(profileId) != true)
                {
                    throw new InvalidOperationException(
                        $"Profile '{profileId}' đang được tác vụ nền sử dụng. Hãy đợi tác vụ kết thúc rồi mở cửa sổ đăng nhập lại.");
                }

                await _processController.CloseContextAsync(profileId, cancellationToken);
                _activeVisibleProfiles.TryRemove(profileId, out _);
                isAlreadyRunning = false;
            }

            if (isAlreadyRunning)
            {
                _activeVisibleProfiles.TryAdd(profileId, 0);

                if (!string.IsNullOrWhiteSpace(targetUrl) && _processController != null && _processController.IsRunning(profileId))
                {
                    _ = _processController.NavigateAsync(profileId, targetUrl, cancellationToken);
                }

                return new BrowserRequestLoginResponseData(
                    Queued: false,
                    VisibilityState: BrowserVisibilityState.VisibleActive,
                    CurrentHolder: profileId);
            }

            // 2. Kiểm tra Ngân sách tài nguyên max_open_browsers
            int maxOpenBrowsers = _getMaxOpenBrowsers?.Invoke() ?? 4;
            int currentOpen = _getOpenBrowserCount?.Invoke() ?? _activeVisibleProfiles.Count;
            if (currentOpen >= maxOpenBrowsers)
            {
                throw new InvalidOperationException(
                    $"Đã đạt giới hạn tối đa {maxOpenBrowsers} trình duyệt hoạt động đồng thời trên toàn hệ thống (ngân sách tài nguyên). Vui lòng đóng bớt cửa sổ trình duyệt hoặc chờ tác vụ khác hoàn tất trước khi mở thêm.");
            }

            // 3. Khởi chạy cửa sổ hiển thị (isHeadless = false) cho profile
            _activeVisibleProfiles.TryAdd(profileId, 0);

            try
            {
                if (_processController != null)
                {
                    if (!_processController.IsRunning(profileId))
                    {
                        await _processController.LaunchLoginWindowAsync(
                            profileId,
                            targetUrl,
                            cancellationToken);
                    }
                    else if (!string.IsNullOrWhiteSpace(targetUrl))
                    {
                        await _processController.NavigateAsync(
                            profileId,
                            targetUrl,
                            cancellationToken: cancellationToken);
                    }
                }
                else if (_profileRegistry != null)
                {
                    await _profileRegistry.UpdateProfileStateAsync(
                        profileId,
                        visibilityState: BrowserVisibilityState.VisibleActive,
                        operationalState: BrowserOperationalState.Running,
                        cancellationToken: cancellationToken);
                }

                return new BrowserRequestLoginResponseData(
                    Queued: false,
                    VisibilityState: BrowserVisibilityState.VisibleActive,
                    CurrentHolder: profileId);
            }
            catch (Exception)
            {
                // Nếu khởi chạy thất bại, giải phóng slot của profile này và cập nhật trạng thái an toàn
                ReleaseVisibleSlot(profileId);
                if (_profileRegistry != null && _processController == null)
                {
                    try
                    {
                        var currentProfile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
                        await _profileRegistry.UpdateProfileStateAsync(
                            profileId,
                            visibilityState: BrowserVisibilityState.Hidden,
                            operationalState: BrowserOperationalState.Stopped,
                            sessionStatus: currentProfile?.SessionStatus ?? BrowserSessionStatus.Unverified,
                            reasonCode: BrowserReasonCode.AuthClosed,
                            cancellationToken: cancellationToken);
                    }
                    catch
                    {
                    }
                }
                throw;
            }
        }
        finally
        {
            profileLock.Release();
        }
    }

    public async Task<BrowserCancelLoginResponseData> CancelLoginAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        await CloseLoginWindowAsync(profileId, BrowserReasonCode.AuthClosed, cancellationToken);
        return new BrowserCancelLoginResponseData(Canceled: true);
    }

    public async Task CloseLoginWindowAsync(
        string profileId,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var profileLock = _profileLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await profileLock.WaitAsync(cancellationToken);
        try
        {
            // Đóng persistent browser context qua controller nếu đang chạy
            if (_processController != null && _processController.IsRunning(profileId))
            {
                try
                {
                    await _processController.CloseContextAsync(profileId, cancellationToken);
                }
                catch
                {
                    // Tiếp tục giải phóng slot dù đóng context lỗi
                }
            }

            ReleaseVisibleSlot(profileId);

            // Probe identity / session status và đưa về Hidden an toàn
            // KHÔNG tự suy luận đăng nhập thành công chỉ vì người dùng đóng cửa sổ!
            string newSessionStatus = BrowserSessionStatus.Unverified;
            string newOperationalState = BrowserOperationalState.Stopped;

            if (_sessionProbe != null)
            {
                try
                {
                    var probeResult = await _sessionProbe.CheckSessionAsync(profileId, cancellationToken);
                    bool isConnected = string.Equals(probeResult.SessionStatus, BrowserSessionStatus.Connected, StringComparison.OrdinalIgnoreCase);
                    bool identityOk = probeResult.IdentityMatches != false;

                    if (isConnected && identityOk)
                    {
                        newSessionStatus = BrowserSessionStatus.Connected;
                        newOperationalState = BrowserOperationalState.Idle;
                    }
                    else if (_profileRegistry != null)
                    {
                        var currentProfile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
                        if (currentProfile != null && !string.IsNullOrWhiteSpace(currentProfile.SessionStatus))
                        {
                            newSessionStatus = currentProfile.SessionStatus;
                        }
                    }
                }
                catch
                {
                    if (_profileRegistry != null)
                    {
                        var currentProfile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
                        if (currentProfile != null && !string.IsNullOrWhiteSpace(currentProfile.SessionStatus))
                        {
                            newSessionStatus = currentProfile.SessionStatus;
                        }
                    }
                }
            }
            else if (_profileRegistry != null)
            {
                var currentProfile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
                if (currentProfile != null && !string.IsNullOrWhiteSpace(currentProfile.SessionStatus))
                {
                    newSessionStatus = currentProfile.SessionStatus;
                    if (string.Equals(reasonCode, "auth_ok", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(newSessionStatus, BrowserSessionStatus.Connected, StringComparison.OrdinalIgnoreCase))
                    {
                        newOperationalState = BrowserOperationalState.Idle;
                    }
                }
            }

            if (_profileRegistry != null)
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profileId,
                    visibilityState: BrowserVisibilityState.Hidden,
                    operationalState: newOperationalState,
                    sessionStatus: newSessionStatus,
                    reasonCode: string.IsNullOrWhiteSpace(reasonCode) ? BrowserReasonCode.AuthClosed : reasonCode,
                    cancellationToken: cancellationToken);
            }
        }
        finally
        {
            profileLock.Release();
        }
    }

    public async Task<bool> WaitForVisibleSlotAsync(
        string profileId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var profileLock = _profileLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        bool lockAcquired = await profileLock.WaitAsync(timeout, cancellationToken);
        if (!lockAcquired) return false;

        try
        {
            if (_activeVisibleProfiles.ContainsKey(profileId))
            {
                return true;
            }

            int maxOpenBrowsers = _getMaxOpenBrowsers?.Invoke() ?? 4;
            int currentOpen = _getOpenBrowserCount?.Invoke() ?? _activeVisibleProfiles.Count;
            if (currentOpen >= maxOpenBrowsers)
            {
                return false;
            }

            _activeVisibleProfiles.TryAdd(profileId, 0);

            if (_profileRegistry != null)
            {
                await _profileRegistry.UpdateProfileStateAsync(
                    profileId,
                    visibilityState: BrowserVisibilityState.VisibleActive,
                    operationalState: BrowserOperationalState.Running,
                    cancellationToken: cancellationToken);
            }

            return true;
        }
        finally
        {
            profileLock.Release();
        }
    }

    public void ReleaseVisibleSlot(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;
        _activeVisibleProfiles.TryRemove(profileId, out _);
    }

    private void OnProcessControllerContextClosed(object? sender, string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;

        // Dọn dẹp slot hiển thị của đúng profileId này
        _activeVisibleProfiles.TryRemove(profileId, out _);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_processController != null)
        {
            _processController.ContextClosed -= OnProcessControllerContextClosed;
        }

        foreach (var sem in _profileLocks.Values)
        {
            sem.Dispose();
        }
        _profileLocks.Clear();
        _activeVisibleProfiles.Clear();
    }
}
