using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Jobs;
using Fraylo.Application.Providers;
using Fraylo.Browser;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Browser;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;

namespace Fraylo.Desktop.Hosting;

/// <summary>
/// Cổng kiểm soát (Gatekeeper) kết nối trực tiếp với Browser Runtime thực tế:
/// ProfileRegistry, LoginCoordinator và TabPool để điều phối quyền chiếm tab và ngăn chặn
/// dispatch khi profile đang Quiescing, VisibleWaiting hoặc VisibleActive.
/// </summary>
public sealed class HostBrowserGatekeeper : IBrowserGatekeeper, IDisposable
{
    private readonly ProfileRegistry _profileRegistry;
    private readonly LoginCoordinator _loginCoordinator;
    private readonly ITabPool _tabPool;
    private readonly IBrowserProcessController? _processController;
    private readonly BrowserSessionCheckpointCoordinator? _checkpointCoordinator;
    private readonly JobActivityLog? _activity;

    private readonly ConcurrentDictionary<string, string> _profileVisibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _profileOperational = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _knownProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileLaunchLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<BrowserProfileStateChangedPayload, Task> _profileStateChangedHandler;

    public HostBrowserGatekeeper(
        ProfileRegistry profileRegistry,
        LoginCoordinator loginCoordinator,
        ITabPool tabPool,
        IBrowserProcessController? processController = null,
        BrowserSessionCheckpointCoordinator? checkpointCoordinator = null,
        JobActivityLog? activity = null)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
        _loginCoordinator = loginCoordinator ?? throw new ArgumentNullException(nameof(loginCoordinator));
        _tabPool = tabPool ?? throw new ArgumentNullException(nameof(tabPool));
        _processController = processController;
        _checkpointCoordinator = checkpointCoordinator;
        _activity = activity;

        _profileStateChangedHandler = payload =>
        {
            if (!string.IsNullOrWhiteSpace(payload.ProfileId))
            {
                _knownProfileIds.TryAdd(payload.ProfileId, 0);
                if (!string.IsNullOrWhiteSpace(payload.VisibilityState))
                {
                    _profileVisibility[payload.ProfileId] = payload.VisibilityState;
                }
                if (!string.IsNullOrWhiteSpace(payload.OperationalState))
                {
                    _profileOperational[payload.ProfileId] = payload.OperationalState;
                }
            }
            return Task.CompletedTask;
        };

        _profileRegistry.ProfileStateChanged += _profileStateChangedHandler;
    }

    public bool IsDispatchPaused(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return false;

        // Nếu profile đang trong quy trình Quiescing hoặc Closing: Tạm dừng dispatch để rút cạn/bảo vệ tài nguyên
        if (_profileOperational.TryGetValue(profileId, out var opState) &&
            (string.Equals(opState, BrowserOperationalState.Quiescing, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(opState, "Closing", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public bool IsProfileBusyWithLogin(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return false;

        // 1. Kiểm tra trạng thái hiển thị của LoginCoordinator cho profileId
        if (_loginCoordinator.IsProfileVisible(profileId))
        {
            return true;
        }

        // 2. Kiểm tra visibility state (VisibleActive hoặc VisibleWaiting cũ)
        if (_profileVisibility.TryGetValue(profileId, out var visState))
        {
            if (string.Equals(visState, BrowserVisibilityState.VisibleActive, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(visState, BrowserVisibilityState.VisibleWaiting, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsRoleActive(string profileId, string role)
    {
        return _tabPool.IsRoleActive(profileId, role);
    }

    public async Task<IDisposable?> TryAcquireTabLeaseAsync(
        string profileId,
        string role,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        // Các job cùng profile có thể cùng thấy browser chưa chạy; khóa này chỉ cho một job mở browser, job sau dùng lại.
        // Việc lấy tab nằm trong cùng khóa để CloseProfileIfIdle không đóng browser giữa lúc tab được cấp.
        var launchLock = GetProfileLaunchLock(profileId);
        await launchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // User-requested trial: show ChatGPT jobs so their browser actions can be inspected.
            if (_processController != null && !_processController.IsRunning(profileId))
            {
                try
                {
                    if (_activity != null)
                        await _activity.WriteAsync(jobId, "browser", "Đang khởi động browser cho tác vụ.", cancellationToken).ConfigureAwait(false);
                    var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
                    // User-requested trial: show the browser for ChatGPT/Gemini text jobs and the MuseAI render.
                    var visibleTrial = (((ChatGptTrialMode.Enabled && profile?.ProviderKey == ProviderKeys.ChatGPTWeb) ||
                        (GeminiTrialMode.Enabled && profile?.ProviderKey == ProviderKeys.GeminiWeb)) &&
                        role is JobTaskType.Script or JobTaskType.Image or JobTaskType.VideoPrompt) ||
                        (MuseAiTrialMode.Enabled && profile?.ProviderKey == ProviderKeys.MuseAI && role == JobTaskType.VideoRender);
                    if (visibleTrial)
                        await _processController.LaunchVisibleAutomationContextAsync(profileId, cancellationToken).ConfigureAwait(false);
                    else
                        await _processController.LaunchPersistentContextAsync(profileId, isHeadless: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (_activity != null)
                        await _activity.WriteAsync(jobId, "browser", "Browser đã khởi động.", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_activity != null)
                        await _activity.WriteAsync(jobId, "error", $"Browser không khởi động được ({ex.GetType().Name}).", CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }

            try
            {
                var handle = await _tabPool.AcquireTabAsync(profileId, role, jobId, cancellationToken).ConfigureAwait(false);
                return new TabLeaseDisposable(async () =>
                {
                    await handle.DisposeAsync().ConfigureAwait(false);
                });
            }
            catch (InvalidOperationException)
            {
                // Role tab đang bận xử lý job khác trong TabPool
                return null;
            }
        }
        finally
        {
            launchLock.Release();
        }
    }

    private SemaphoreSlim GetProfileLaunchLock(string profileId) =>
        _profileLaunchLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));

    public void ReleaseTabLease(string profileId, string role, string jobId)
    {
        _ = _tabPool.ReleaseTabAsync(profileId, role, jobId, CancellationToken.None);
    }

    public bool IsProfileIdle(string profileId)
    {
        // 1. Kiểm tra số tab đang hoạt động trong TabPool
        if (_tabPool.GetActiveJobCount(profileId) > 0)
        {
            return false;
        }

        // 2. Không đóng browser khi còn job chạy hoặc job chưa rõ kết quả cần đối soát
        if (_checkpointCoordinator != null && _checkpointCoordinator.HasUncertainOrActiveJobs(profileId))
        {
            return false;
        }

        return true;
    }

    public void CloseProfileIfIdle(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;

        if (IsProfileIdle(profileId) && _processController != null && _processController.IsRunning(profileId))
        {
            // Đóng dưới cùng khóa với launch/acquire và kiểm tra lại idle: job vừa được cấp tab sẽ đợi, không bị đóng giữa chừng
            _ = Task.Run(async () =>
            {
                var launchLock = GetProfileLaunchLock(profileId);
                await launchLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!IsProfileIdle(profileId) || !_processController.IsRunning(profileId)) return;

                    _profileOperational[profileId] = "Closing";
                    await _processController.CloseContextAsync(profileId, CancellationToken.None).ConfigureAwait(false);
                    _profileOperational[profileId] = BrowserOperationalState.Stopped;
                }
                catch (Exception ex)
                {
                    _profileOperational[profileId] = BrowserOperationalState.Error;
                    System.Diagnostics.Debug.WriteLine($"[HostBrowserGatekeeper] Lỗi khi đóng idle browser context cho profile {profileId}: {ex.Message}");
                }
                finally
                {
                    launchLock.Release();
                }
            });
        }
    }

    public IReadOnlySet<string> GetActiveOrBusyProfileIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Toàn bộ các profile đang hiển thị cửa sổ từ login coordinator
        foreach (var visibleProfileId in _loginCoordinator.ActiveVisibleProfileIds)
        {
            if (!string.IsNullOrWhiteSpace(visibleProfileId))
            {
                set.Add(visibleProfileId);
            }
        }

        // 2. Profile đang VisibleActive hoặc VisibleWaiting
        foreach (var kvp in _profileVisibility)
        {
            if (string.Equals(kvp.Value, BrowserVisibilityState.VisibleActive, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Value, BrowserVisibilityState.VisibleWaiting, StringComparison.OrdinalIgnoreCase))
            {
                set.Add(kvp.Key);
            }
        }

        // 3. Profile đang Quiescing, Closing hoặc Running
        foreach (var kvp in _profileOperational)
        {
            if (string.Equals(kvp.Value, BrowserOperationalState.Quiescing, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Value, "Closing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Value, BrowserOperationalState.Running, StringComparison.OrdinalIgnoreCase))
            {
                set.Add(kvp.Key);
            }
        }

        // 4. Profile đang có tiến trình Playwright persistent context chạy hoặc tab pool đang hoạt động
        foreach (var profileId in _knownProfileIds.Keys)
        {
            if (_processController != null && _processController.IsRunning(profileId))
            {
                set.Add(profileId);
            }
            if (_tabPool.GetActiveJobCount(profileId) > 0)
            {
                set.Add(profileId);
            }
        }

        return set;
    }

    public void TrackJob(string profileId, string jobId, bool sideEffectSubmitted = false, string state = "Running")
    {
        _checkpointCoordinator?.TrackJob(profileId, jobId, sideEffectSubmitted, state);
    }

    public void SetJobSideEffectSubmitted(string profileId, string jobId, bool submitted = true)
    {
        _checkpointCoordinator?.SetJobSideEffectSubmitted(profileId, jobId, submitted);
    }

    public void UntrackJob(string profileId, string jobId)
    {
        _checkpointCoordinator?.UntrackJob(profileId, jobId);
    }

    public bool CanResumeJob(string profileId, string jobId, bool sideEffectSubmitted)
    {
        return _checkpointCoordinator?.CanResumeJob(profileId, jobId, sideEffectSubmitted) ?? true;
    }

    public void Dispose()
    {
        _profileRegistry.ProfileStateChanged -= _profileStateChangedHandler;
    }

    private sealed class TabLeaseDisposable : IDisposable
    {
        private Func<Task>? _releaseAction;
        public TabLeaseDisposable(Func<Task> releaseAction) => _releaseAction = releaseAction;

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _releaseAction, null);
            if (action != null)
            {
                _ = action();
            }
        }
    }
}
