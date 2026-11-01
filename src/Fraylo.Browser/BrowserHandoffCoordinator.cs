using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Browser;

/// <summary>
/// Điều phối quy trình Quiescing 4 bước và handoff sang cửa sổ đăng nhập hiển thị.
/// Quản lý checkpoint chuyển giao trong SQLite để phục hồi sau sự cố.
/// </summary>
public sealed class BrowserHandoffCoordinator : IBrowserPauseDrainController
{
    private readonly IProfileRegistry _profileRegistry;
    private readonly ITabPool _tabPool;
    private readonly ILoginCoordinator _loginCoordinator;
    private readonly Func<FrayloDbContext> _dbContextFactory;
    private readonly ISessionProbe _sessionProbe;

    private readonly ConcurrentDictionary<string, bool> _pausedProfiles = new();

    public BrowserHandoffCoordinator(
        IProfileRegistry profileRegistry,
        ITabPool tabPool,
        ILoginCoordinator loginCoordinator,
        Func<FrayloDbContext> dbContextFactory,
        ISessionProbe? sessionProbe = null)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
        _tabPool = tabPool ?? throw new ArgumentNullException(nameof(tabPool));
        _loginCoordinator = loginCoordinator ?? throw new ArgumentNullException(nameof(loginCoordinator));
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _sessionProbe = sessionProbe ?? new SessionProbe(profileRegistry);
    }

    public bool IsDispatchPaused(string profileId)
    {
        return _pausedProfiles.TryGetValue(profileId, out var paused) && paused;
    }

    public async Task PauseDispatchAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        _pausedProfiles[profileId] = true;

        await _profileRegistry.UpdateProfileStateAsync(
            profileId,
            operationalState: BrowserOperationalState.Quiescing,
            cancellationToken: cancellationToken);
    }

    public async Task DrainPendingTabsAsync(
        string profileId,
        Func<string, Task>? onTabDrain = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var activeTabs = _tabPool.GetAllActiveTabs(profileId);
        foreach (var tab in activeTabs)
        {
            // Các tab chưa submit side-effect (side_effect_submitted == 0) được dừng an toàn
            if (!tab.IsSideEffectSubmitted)
            {
                if (onTabDrain != null)
                {
                    await onTabDrain(tab.JobId);
                }

                await _tabPool.ReleaseTabAsync(profileId, tab.Role, cancellationToken);
            }
        }
    }

    public async Task WaitForInFlightTabsAsync(
        string profileId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_tabPool.GetActiveJobCount(profileId) == 0)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    public async Task PrepareHandoffToVisibleLoginAsync(
        string profileId,
        string? activeJobId,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
        string previousState = profile?.OperationalState ?? BrowserOperationalState.Stopped;

        // Lưu checkpoint chuyển giao vào SQLite
        await using (var db = _dbContextFactory())
        {
            var checkpoint = new BrowserHandoffCheckpoint
            {
                Id = Guid.NewGuid().ToString(),
                ProfileId = profileId,
                ActiveJobId = activeJobId,
                PreviousState = previousState,
                TargetVisibilityState = BrowserVisibilityState.VisibleActive,
                ReasonCode = reasonCode,
                CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                ResolvedAtUtc = null
            };

            db.BrowserHandoffCheckpoints.Add(checkpoint);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Đánh dấu Quiescing
        await _profileRegistry.UpdateProfileStateAsync(
            profileId,
            operationalState: BrowserOperationalState.Quiescing,
            cancellationToken: cancellationToken);

        // Yêu cầu slot hiển thị từ LoginCoordinator
        await _loginCoordinator.RequestLoginAsync(profileId, cancellationToken);
    }

    public async Task CompleteLoginHandoffAsync(
        string profileId,
        bool authSuccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        // Bước 1: Đóng cửa sổ đăng nhập hiển thị, giải phóng Semaphore, chuyển trạng thái hiển thị về Hidden.
        await _loginCoordinator.CloseLoginWindowAsync(
            profileId,
            authSuccess ? "auth_ok" : BrowserReasonCode.AuthClosed,
            cancellationToken);

        await _profileRegistry.UpdateProfileStateAsync(
            profileId,
            visibilityState: BrowserVisibilityState.Hidden,
            cancellationToken: cancellationToken);

        // Bước 2: Thẩm định trạng thái session thực tế thông qua ISessionProbe.CheckSessionAsync(profileId).
        var sessionCheck = await _sessionProbe.CheckSessionAsync(profileId, cancellationToken);

        // Bước 3: CHỈ KHI probe xác nhận sessionStatus == BrowserSessionStatus.Connected và danh tính khớp
        // thì mới cập nhật ResolvedAtUtc cho BrowserHandoffCheckpoint trong SQLite, và đưa sessionStatus = Connected, operationalState = Idle.
        bool isProbeConnected = string.Equals(sessionCheck.SessionStatus, BrowserSessionStatus.Connected, StringComparison.OrdinalIgnoreCase);
        bool identityMatches = sessionCheck.IdentityMatches != false;

        if (authSuccess && isProbeConnected && identityMatches)
        {
            string nowUtc = DateTime.UtcNow.ToString("o");

            await using (var db = _dbContextFactory())
            {
                var pendingCheckpoints = await db.BrowserHandoffCheckpoints
                    .Where(c => c.ProfileId == profileId && c.ResolvedAtUtc == null)
                    .ToListAsync(cancellationToken);

                foreach (var cp in pendingCheckpoints)
                {
                    cp.ResolvedAtUtc = nowUtc;
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                visibilityState: BrowserVisibilityState.Hidden,
                operationalState: BrowserOperationalState.Idle,
                sessionStatus: BrowserSessionStatus.Connected,
                reasonCode: null,
                cancellationToken: cancellationToken);
        }
        else
        {
            // Bước 4: Nếu probe trả về Unverified hoặc SessionExpired (kể cả khi authSuccess == true),
            // tuyệt đối KHÔNG resolve checkpoint như là thành công. Cập nhật checkpoint với reasonCode login_unverified
            // hoặc ghi nhận chưa hoàn tất, đưa sessionStatus = Unverified, operationalState = Stopped.
            const string unverifiedReason = "login_unverified";

            await using (var db = _dbContextFactory())
            {
                var pendingCheckpoints = await db.BrowserHandoffCheckpoints
                    .Where(c => c.ProfileId == profileId && c.ResolvedAtUtc == null)
                    .ToListAsync(cancellationToken);

                foreach (var cp in pendingCheckpoints)
                {
                    cp.ReasonCode = unverifiedReason;
                    // Tuyệt đối KHÔNG gán cp.ResolvedAtUtc để ghi nhận chưa hoàn tất thành công!
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                visibilityState: BrowserVisibilityState.Hidden,
                operationalState: BrowserOperationalState.Stopped,
                sessionStatus: BrowserSessionStatus.Unverified,
                reasonCode: unverifiedReason,
                cancellationToken: cancellationToken);
        }

        // Bỏ cờ tạm dừng dispatch (_pausedProfiles).
        _pausedProfiles.TryRemove(profileId, out _);
    }
}
