using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TiToAI.Browser.Abstractions;
using TiToAI.Browser.Models;
using TiToAI.Contracts.Browser;
using TiToAI.Contracts.Jobs;
using TiToAI.Contracts.Providers;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Browser;

/// <summary>
/// Kết quả xác minh đăng nhập và thẩm định danh tính sau login thủ công.
/// </summary>
public sealed record BrowserLoginVerificationResult(
    bool Success,
    string SessionStatus,
    bool IdentityMatches,
    string? ReasonCode = null,
    string? ErrorMessage = null)
{
    public static implicit operator bool(BrowserLoginVerificationResult result) => result.Success;
}

/// <summary>
/// Trạng thái theo dõi tác vụ nội bộ tại coordinator.
/// </summary>
public sealed class JobSessionTrackingInfo
{
    public string JobId { get; }
    public string ProfileId { get; }
    public bool SideEffectSubmitted { get; set; }
    public string State { get; set; }
    public string? ReasonCode { get; set; }

    public JobSessionTrackingInfo(string jobId, string profileId, bool sideEffectSubmitted, string state, string? reasonCode = null)
    {
        JobId = jobId;
        ProfileId = profileId;
        SideEffectSubmitted = sideEffectSubmitted;
        State = state;
        ReasonCode = reasonCode;
    }
}

/// <summary>
/// Điều phối Session Checkpoint, Profile Isolation, Quiescing an toàn và Handoff Login cho Task T16.
/// Đảm bảo:
/// 1. Mất session chỉ cách ly profile tương ứng; các profile khác không bị ảnh hưởng (không pause toàn app).
/// 2. Quiesce an toàn: không cắt ngang hay đóng ép buộc khi tác vụ đang có side-effect chưa rõ kết quả.
/// 3. Xác minh identity sau login thủ công: bắt buộc đóng cửa sổ trình duyệt về Hidden trước khi cho phép resume job.
/// 4. Phát hiện sai identity: đánh dấu wrong_account, chặn đứng resume.
/// 5. Đóng nút X khi login: chuyển auth_closed, bảo toàn session/cookie, không tự động retry/resend.
/// </summary>
public sealed class BrowserSessionCheckpointCoordinator : IDisposable
{
    private readonly IProfileRegistry _profileRegistry;
    private readonly ILoginCoordinator _loginCoordinator;
    private readonly ISessionProbe _sessionProbe;
    private readonly IBrowserProcessController? _processController;
    private readonly ITabPool? _tabPool;
    private readonly Func<TiToAIDbContext>? _dbContextFactory;

    // Theo dõi job active theo profile (hỗ trợ in-memory tracking độc lập)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JobSessionTrackingInfo>> _profileJobs = new();

    // Lưu mã lý do mất session đang diễn ra trên từng profile
    private readonly ConcurrentDictionary<string, string> _profileSessionLossReason = new();

    // Đánh dấu các lệnh đóng cửa sổ chủ động từ coordinator để phân biệt với nút X
    private readonly ConcurrentDictionary<string, bool> _programmaticClosing = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileLocks = new();
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    private bool _disposed;

    public BrowserSessionCheckpointCoordinator(
        IProfileRegistry profileRegistry,
        ILoginCoordinator loginCoordinator,
        ISessionProbe? sessionProbe = null,
        IBrowserProcessController? processController = null,
        ITabPool? tabPool = null,
        Func<TiToAIDbContext>? dbContextFactory = null)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
        _loginCoordinator = loginCoordinator ?? throw new ArgumentNullException(nameof(loginCoordinator));
        _sessionProbe = sessionProbe ?? new SessionProbe(profileRegistry);
        _processController = processController;
        _tabPool = tabPool;
        _dbContextFactory = dbContextFactory;

        if (_processController != null)
        {
            _processController.ContextClosed += OnProcessControllerContextClosed;
        }
    }

    /// <summary>
    /// Đăng ký hoặc cập nhật thông tin theo dõi tác vụ gắn với profile.
    /// </summary>
    public void TrackJob(string profileId, string jobId, bool sideEffectSubmitted = false, string state = JobState.Running)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var jobs = _profileJobs.GetOrAdd(profileId, _ => new ConcurrentDictionary<string, JobSessionTrackingInfo>(StringComparer.OrdinalIgnoreCase));
        jobs.AddOrUpdate(
            jobId,
            _ => new JobSessionTrackingInfo(jobId, profileId, sideEffectSubmitted, state),
            (_, existing) =>
            {
                existing.SideEffectSubmitted = sideEffectSubmitted;
                existing.State = state;
                return existing;
            });
    }

    /// <summary>
    /// Cập nhật cờ side effect đã gửi của một job.
    /// </summary>
    public void SetJobSideEffectSubmitted(string profileId, string jobId, bool submitted = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (_profileJobs.TryGetValue(profileId, out var jobs) && jobs.TryGetValue(jobId, out var info))
        {
            info.SideEffectSubmitted = submitted;
        }

        if (_dbContextFactory != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _dbLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await using var db = _dbContextFactory();
                        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId).ConfigureAwait(false);
                        if (job != null)
                        {
                            job.SideEffectSubmitted = submitted ? 1 : 0;
                            await db.SaveChangesAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _dbLock.Release();
                    }
                }
                catch
                {
                }
            });
        }
    }

    /// <summary>
    /// Lấy trạng thái của một job được theo dõi trong coordinator (in-memory).
    /// </summary>
    public JobSessionTrackingInfo? GetTrackedJob(string profileId, string jobId)
    {
        if (_profileJobs.TryGetValue(profileId, out var jobs) && jobs.TryGetValue(jobId, out var info))
        {
            return info;
        }
        return null;
    }

    /// <summary>
    /// Xóa theo dõi một job khi hoàn tất xử lý hoặc giải phóng tab lease.
    /// </summary>
    public void UntrackJob(string profileId, string jobId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(jobId)) return;
        if (_profileJobs.TryGetValue(profileId, out var jobs))
        {
            jobs.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// Kiểm tra xem profile có bất kỳ tác vụ nào đang hoạt động (Running/Reserved)
    /// hoặc tác vụ đang chờ đối soát kết quả (AwaitingUser với SideEffectSubmitted == 1).
    /// Tuyệt đối không đóng browser nếu còn các tác vụ này.
    /// </summary>
    public bool HasUncertainOrActiveJobs(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return false;

        // 1. Kiểm tra in-memory tracking
        if (_profileJobs.TryGetValue(profileId, out var jobs))
        {
            foreach (var kvp in jobs)
            {
                var job = kvp.Value;
                if (string.Equals(job.State, JobState.Running, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(job.State, JobState.Reserved, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (job.SideEffectSubmitted &&
                    (string.Equals(job.State, JobState.AwaitingUser, StringComparison.OrdinalIgnoreCase) ||
                     (job.State != JobState.Succeeded && job.State != JobState.Failed && job.State != JobState.Canceled)))
                {
                    return true;
                }
            }
        }

        // 2. Kiểm tra DB nếu có dbContextFactory
        if (_dbContextFactory != null)
        {
            try
            {
                using var db = _dbContextFactory();
                var accountIds = db.ProviderAccounts
                    .Where(a => a.ProfileId == profileId)
                    .Select(a => a.Id)
                    .ToList();

                if (accountIds.Count > 0)
                {
                    // MuseAI: side chat đã lưu URL và việc khôi phục mở lại browser qua lease, nên job chờ đối soát không giữ browser.
                    bool hasActiveOrUncertain = db.Jobs.Any(j =>
                        j.ExecutionBinding != null &&
                        accountIds.Contains(j.ExecutionBinding.ProviderAccountId) &&
                        (j.State == JobState.Running ||
                         j.State == JobState.Reserved ||
                         (j.State == JobState.AwaitingUser && j.SideEffectSubmitted == 1 && j.ProviderKey != ProviderKeys.MuseAI)));

                    if (hasActiveOrUncertain) return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    /// <summary>
    /// Xử lý mất session (session_expired, login_required, auth_closed).
    /// Tuân thủ nghiêm ngặt nguyên tắc Profile Isolation:
    /// - CHỈ ảnh hưởng đến profile đó và các tab/job đang thuộc về profile đó.
    /// - CÁC PROFILE KHÁC HOÀN TOÀN KHÔNG BỊ ẢNH HƯỞNG (không pause toàn app).
    /// - Tác vụ trên profile mất session chuyển sang AwaitingUser với reason_code tương ứng.
    /// </summary>
    public async Task HandleSessionLossAsync(
        string profileId,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            reasonCode = BrowserReasonCode.SessionExpired;
        }

        _profileSessionLossReason[profileId] = reasonCode;

        // 1. Cập nhật trạng thái Profile bị mất session
        string newSessionStatus = reasonCode switch
        {
            BrowserReasonCode.SessionExpired => BrowserSessionStatus.SessionExpired,
            BrowserReasonCode.LoginRequired => BrowserSessionStatus.SessionExpired,
            _ => BrowserSessionStatus.Unverified
        };

        await _profileRegistry.UpdateProfileStateAsync(
            profileId,
            visibilityState: BrowserVisibilityState.Hidden,
            operationalState: BrowserOperationalState.Stopped,
            sessionStatus: newSessionStatus,
            reasonCode: reasonCode,
            cancellationToken: cancellationToken);

        // 2. Chuyển các job thuộc profile này sang AwaitingUser với reasonCode
        // a) Cập nhật SQLite qua DbContext nếu có
        if (_dbContextFactory != null)
        {
            await _dbLock.WaitAsync(cancellationToken);
            try
            {
                await using var db = _dbContextFactory();
                var boundJobIds = await db.ExecutionBindings
                    .Where(b => b.ProfileId == profileId)
                    .Select(b => b.JobId)
                    .ToListAsync(cancellationToken);

                if (boundJobIds.Count > 0)
                {
                    var jobsToUpdate = await db.Jobs
                        .Where(j => boundJobIds.Contains(j.Id) &&
                                    (j.State == JobState.Running || j.State == JobState.Reserved || j.State == JobState.Queued))
                        .ToListAsync(cancellationToken);

                    string nowIso = DateTime.UtcNow.ToString("o");
                    foreach (var job in jobsToUpdate)
                    {
                        job.State = JobState.AwaitingUser;
                        bool hasInFlightSideEffect = (job.SideEffectSubmitted == 1 && string.IsNullOrEmpty(job.ResultReference)) ||
                            (_profileJobs.TryGetValue(profileId, out var pJobs) && pJobs.TryGetValue(job.Id, out var tInfo) && tInfo.SideEffectSubmitted);

                        job.ReasonCode = hasInFlightSideEffect ? JobReasonCode.ReconcileRequired : reasonCode;
                        job.UpdatedAtUtc = nowIso;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            finally
            {
                _dbLock.Release();
            }
        }

        // b) Cập nhật tracking trong memory cho CHỈ profile này
        if (_profileJobs.TryGetValue(profileId, out var jobs))
        {
            foreach (var kvp in jobs)
            {
                var info = kvp.Value;
                if (info.State == JobState.Running || info.State == JobState.Reserved || info.State == JobState.Queued)
                {
                    info.State = JobState.AwaitingUser;
                    info.ReasonCode = info.SideEffectSubmitted ? JobReasonCode.ReconcileRequired : reasonCode;
                }
            }
        }

        // LƯU Ý: CÁC PROFILE KHÁC HOÀN TOÀN KHÔNG BỊ ẢNH HƯỞNG (Isolation Rule).
    }

    /// <summary>
    /// Kiểm tra xem profile có thể Quiesce / đóng ngữ cảnh an toàn hay không.
    /// Không cắt ngang tác vụ đang có side-effect chưa rõ kết quả.
    /// Nếu tab hoặc job đang ở giữa pre-commit side effect hoặc đang chờ ack từ provider thì giữ ngữ cảnh, không đóng ép buộc.
    /// </summary>
    public bool CanSafelyQuiesce(string profileId, IEnumerable<string>? activeJobIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        // 1. Kiểm tra TabPool nếu có
        if (_tabPool != null)
        {
            var activeTabs = _tabPool.GetAllActiveTabs(profileId);
            foreach (var tab in activeTabs)
            {
                if (tab.IsSideEffectSubmitted)
                {
                    // Tab đang có side effect chưa hoàn tất -> Không được quiesce bạo lực
                    return false;
                }
            }
        }

        // 2. Kiểm tra activeJobIds hoặc in-memory jobs
        var jobIdsToCheck = activeJobIds?.ToList();
        if (jobIdsToCheck != null && jobIdsToCheck.Count > 0)
        {
            if (_profileJobs.TryGetValue(profileId, out var profileJobs))
            {
                foreach (var jobId in jobIdsToCheck)
                {
                    if (profileJobs.TryGetValue(jobId, out var info) && info.SideEffectSubmitted)
                    {
                        return false;
                    }
                }
            }

            if (_dbContextFactory != null)
            {
                using var db = _dbContextFactory();
                bool hasUnconfirmedSideEffect = db.Jobs.Any(j =>
                    jobIdsToCheck.Contains(j.Id) &&
                    j.SideEffectSubmitted == 1 &&
                    string.IsNullOrEmpty(j.ResultReference));

                if (hasUnconfirmedSideEffect)
                {
                    return false;
                }
            }
        }
        else if (_profileJobs.TryGetValue(profileId, out var profileJobs))
        {
            foreach (var kvp in profileJobs)
            {
                var job = kvp.Value;
                if (job.SideEffectSubmitted && job.State != JobState.Succeeded && job.State != JobState.Failed && job.State != JobState.Canceled)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Yêu cầu mở cửa sổ đăng nhập hiển thị cho profile (Global One-Visible-Login Rule).
    /// </summary>
    public async Task<BrowserRequestLoginResponseData> RequestLoginAsync(
        string profileId,
        string? targetUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return await _loginCoordinator.RequestLoginAsync(profileId, targetUrl, cancellationToken);
    }

    /// <summary>
    /// Xác minh kết quả đăng nhập thủ công qua ISessionProbe và xử lý trạng thái theo Acceptance Criteria:
    /// - TH1: Xác minh thành công và đúng Identity (identityMatches == true):
    ///   BẮT BUỘC ĐÓNG CỬA SỔ TRÌNH DUYỆT (hoặc chuyển về Hidden) TRƯỚC KHI cho phép resume job hoặc tiếp tục automation!
    ///   Cập nhật Profile: VisibilityState = Hidden, OperationalState = Stopped, SessionStatus = Connected.
    ///   Lúc này các job đang AwaitingUser (do login_required) trên profile này mới đủ điều kiện được Resume.
    /// - TH2: Sai Identity (identityMatches == false):
    ///   Đóng cửa sổ hiển thị.
    ///   Đánh dấu reason_code = 'wrong_account'.
    ///   TUYỆT ĐỐI KHÔNG được resume job! Báo sai tài khoản và yêu cầu đăng nhập đúng tài khoản đã liên kết.
    /// - TH3: Đóng nút X trước khi xác minh:
    ///   Cập nhật Profile sang Hidden/Stopped với reason_code = 'auth_closed'.
    ///   TUYỆT ĐỐI KHÔNG tự động retry / resend job!
    /// </summary>
    public async Task<BrowserLoginVerificationResult> VerifyAndCompleteLoginAsync(
        string profileId,
        string expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        // Bước 1: Gọi probe kiểm tra session và identity
        var probeResult = await _sessionProbe.CheckSessionAsync(profileId, cancellationToken);
        bool isConnected = string.Equals(probeResult.SessionStatus, BrowserSessionStatus.Connected, StringComparison.OrdinalIgnoreCase);

        // Đánh giá identity match
        bool identityMatches;
        if (probeResult.IdentityMatches.HasValue)
        {
            identityMatches = probeResult.IdentityMatches.Value;
        }
        else
        {
            var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(expectedIdentity) && !string.IsNullOrWhiteSpace(profile?.AccountIdentity))
            {
                identityMatches = string.Equals(profile.AccountIdentity, expectedIdentity, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                identityMatches = true;
            }
        }

        // TH1: Xác minh thành công và đúng Identity
        if (isConnected && identityMatches)
        {
            _programmaticClosing[profileId] = true;
            try
            {
                // BẮT BUỘC ĐÓNG CỬA SỔ TRÌNH DUYỆT TRƯỚC KHI cho phép resume job hoặc tiếp tục automation!
                await _loginCoordinator.CloseLoginWindowAsync(profileId, "auth_ok", cancellationToken);
            }
            finally
            {
                _programmaticClosing.TryRemove(profileId, out _);
            }

            // Cập nhật Profile: VisibilityState = Hidden, OperationalState = Stopped, SessionStatus = Connected
            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                visibilityState: BrowserVisibilityState.Hidden,
                operationalState: BrowserOperationalState.Stopped,
                sessionStatus: BrowserSessionStatus.Connected,
                accountIdentity: !string.IsNullOrWhiteSpace(expectedIdentity) ? expectedIdentity : null,
                reasonCode: null,
                cancellationToken: cancellationToken);

            _profileSessionLossReason.TryRemove(profileId, out _);

            return new BrowserLoginVerificationResult(
                Success: true,
                SessionStatus: BrowserSessionStatus.Connected,
                IdentityMatches: true,
                ReasonCode: null);
        }

        // TH2: Sai Identity (identityMatches == false)
        if (!identityMatches)
        {
            _programmaticClosing[profileId] = true;
            try
            {
                // Đóng cửa sổ hiển thị
                await _loginCoordinator.CloseLoginWindowAsync(profileId, BrowserReasonCode.WrongAccount, cancellationToken);
            }
            finally
            {
                _programmaticClosing.TryRemove(profileId, out _);
            }

            // Đánh dấu reason_code = 'wrong_account'
            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                visibilityState: BrowserVisibilityState.Hidden,
                operationalState: BrowserOperationalState.Stopped,
                sessionStatus: BrowserSessionStatus.Unverified,
                reasonCode: BrowserReasonCode.WrongAccount,
                cancellationToken: cancellationToken);

            _profileSessionLossReason[profileId] = BrowserReasonCode.WrongAccount;

            // Chuyển/giữ các job thuộc profile này ở AwaitingUser với reason_code = 'wrong_account'
            await UpdateProfileJobsStateAsync(profileId, JobState.AwaitingUser, BrowserReasonCode.WrongAccount, cancellationToken);

            return new BrowserLoginVerificationResult(
                Success: false,
                SessionStatus: probeResult.SessionStatus,
                IdentityMatches: false,
                ReasonCode: BrowserReasonCode.WrongAccount,
                ErrorMessage: $"Đăng nhập sai tài khoản. Yêu cầu đăng nhập đúng tài khoản '{expectedIdentity}'.");
        }

        // TH khác: Phiên chưa kết nối hợp lệ
        _programmaticClosing[profileId] = true;
        try
        {
            await _loginCoordinator.CloseLoginWindowAsync(profileId, BrowserReasonCode.LoginRequired, cancellationToken);
        }
        finally
        {
            _programmaticClosing.TryRemove(profileId, out _);
        }

        await _profileRegistry.UpdateProfileStateAsync(
            profileId,
            visibilityState: BrowserVisibilityState.Hidden,
            operationalState: BrowserOperationalState.Stopped,
            sessionStatus: BrowserSessionStatus.Unverified,
            reasonCode: BrowserReasonCode.LoginRequired,
            cancellationToken: cancellationToken);

        _profileSessionLossReason[profileId] = BrowserReasonCode.LoginRequired;

        return new BrowserLoginVerificationResult(
            Success: false,
            SessionStatus: BrowserSessionStatus.Unverified,
            IdentityMatches: identityMatches,
            ReasonCode: BrowserReasonCode.LoginRequired,
            ErrorMessage: "Phiên đăng nhập chưa được kết nối hợp lệ.");
    }

    /// <summary>
    /// Xử lý TH3: Người dùng đóng cửa sổ đăng nhập bằng nút X trước khi xác minh.
    /// Tận dụng sự kiện context closed (cơ chế BRW-1):
    /// - Cập nhật Profile sang Hidden/Stopped với reason_code = 'auth_closed'.
    /// - TUYỆT ĐỐI KHÔNG tự động retry / resend job!
    /// - Job tiếp tục giữ ở AwaitingUser với reason_code = 'auth_closed'.
    /// - Cookie/session hiện tại được bảo toàn (không bị logout ngoài ý muốn).
    /// - Người dùng có thể bấm nút Đăng nhập lại bất cứ lúc nào.
    /// </summary>
    public async Task HandleWindowClosedByXAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var gate = _profileLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Đóng login window giải phóng semaphore
            await _loginCoordinator.CloseLoginWindowAsync(profileId, BrowserReasonCode.AuthClosed, cancellationToken);

            // Giữ nguyên SessionStatus hiện tại (bảo toàn cookie/session)
            var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
            string currentSessionStatus = profile?.SessionStatus ?? BrowserSessionStatus.Unverified;

            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                visibilityState: BrowserVisibilityState.Hidden,
                operationalState: BrowserOperationalState.Stopped,
                sessionStatus: currentSessionStatus,
                reasonCode: BrowserReasonCode.AuthClosed,
                cancellationToken: cancellationToken);

            _profileSessionLossReason[profileId] = BrowserReasonCode.AuthClosed;

            // TUYỆT ĐỐI KHÔNG tự động retry / resend job!
            // Giữ job ở AwaitingUser với reason_code = 'auth_closed'
            await UpdateProfileJobsStateAsync(profileId, JobState.AwaitingUser, BrowserReasonCode.AuthClosed, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Kiểm tra xem một tác vụ trên profile có đủ điều kiện được resume hay không.
    /// Điều kiện tiên quyết:
    /// 1. Cửa sổ trình duyệt BẮT BUỘC phải ở trạng thái Hidden (tuyệt đối không cho phép automation khi cửa sổ visible).
    /// 2. SessionStatus phải là Connected (đã xác minh phiên hợp lệ).
    /// 3. Không có cờ chặn như wrong_account hoặc auth_closed.
    /// 
    /// Khi đủ điều kiện:
    /// - Nếu sideEffectSubmitted == false: Đủ điều kiện resume chuyển về Queued để gửi an toàn.
    /// - Nếu sideEffectSubmitted == true: Đủ điều kiện resume chuyển về Reconcile để đối soát, CẤM retry mù.
    /// </summary>
    public bool CanResumeJob(string profileId, string jobId, bool sideEffectSubmitted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        // 1. Kiểm tra trạng thái Profile
        var profile = _profileRegistry.GetProfileAsync(profileId).GetAwaiter().GetResult();
        if (profile == null)
        {
            return false;
        }

        // BẮT BUỘC: Cửa sổ trình duyệt phải ở trạng thái Hidden
        if (!string.Equals(profile.VisibilityState, BrowserVisibilityState.Hidden, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Profile không được đang nắm giữ slot hiển thị
        if (string.Equals(_loginCoordinator.CurrentVisibleProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
            _loginCoordinator.IsProfileVisible(profileId))
        {
            return false;
        }

        // BẮT BUỘC: Session phải là Connected
        if (!string.Equals(profile.SessionStatus, BrowserSessionStatus.Connected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Chặn nếu đang có reason_code wrong_account hoặc auth_closed
        if (_profileSessionLossReason.TryGetValue(profileId, out var lossReason))
        {
            if (string.Equals(lossReason, BrowserReasonCode.WrongAccount, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lossReason, BrowserReasonCode.AuthClosed, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Async overload của <see cref="CanResumeJob(string, string, bool)"/>.
    /// </summary>
    public async Task<bool> CanResumeJobAsync(
        string profileId,
        string jobId,
        bool sideEffectSubmitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
        if (profile == null)
        {
            return false;
        }

        if (!string.Equals(profile.VisibilityState, BrowserVisibilityState.Hidden, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(_loginCoordinator.CurrentVisibleProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
            _loginCoordinator.IsProfileVisible(profileId))
        {
            return false;
        }

        if (!string.Equals(profile.SessionStatus, BrowserSessionStatus.Connected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_profileSessionLossReason.TryGetValue(profileId, out var lossReason))
        {
            if (string.Equals(lossReason, BrowserReasonCode.WrongAccount, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lossReason, BrowserReasonCode.AuthClosed, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Xác định trạng thái đích khi resume tác vụ:
    /// - "Queued": Nếu chưa submit side effect (được xếp hàng lại an toàn).
    /// - "Reconcile": Nếu đã submit side effect (bắt buộc đối soát kết quả, CẤM retry mù).
    /// - "Blocked": Nếu không đủ điều kiện resume (chưa login, sai tài khoản, auth_closed, hoặc cửa sổ chưa ẩn).
    /// </summary>
    public string GetResumeTargetState(string profileId, string jobId, bool sideEffectSubmitted)
    {
        if (!CanResumeJob(profileId, jobId, sideEffectSubmitted))
        {
            return "Blocked";
        }

        if (!sideEffectSubmitted)
        {
            if (_profileJobs.TryGetValue(profileId, out var trackedJobs) &&
                trackedJobs.TryGetValue(jobId, out var trackedInfo) &&
                trackedInfo.SideEffectSubmitted)
            {
                sideEffectSubmitted = true;
            }
        }

        return sideEffectSubmitted ? "Reconcile" : JobState.Queued;
    }

    /// <summary>
    /// Thực hiện resume tác vụ về trạng thái tương ứng sau khi kiểm tra đủ điều kiện.
    /// </summary>
    public async Task<string> ResumeJobAsync(
        string profileId,
        string jobId,
        bool sideEffectSubmitted,
        CancellationToken cancellationToken = default)
    {
        if (!await CanResumeJobAsync(profileId, jobId, sideEffectSubmitted, cancellationToken))
        {
            return "Blocked";
        }

        if (!sideEffectSubmitted)
        {
            if (_profileJobs.TryGetValue(profileId, out var trackedJobs) &&
                trackedJobs.TryGetValue(jobId, out var trackedInfo) &&
                trackedInfo.SideEffectSubmitted)
            {
                sideEffectSubmitted = true;
            }
        }

        string targetState = sideEffectSubmitted ? "Reconcile" : JobState.Queued;

        // Cập nhật in-memory
        if (_profileJobs.TryGetValue(profileId, out var jobs) && jobs.TryGetValue(jobId, out var info))
        {
            info.State = targetState;
            info.ReasonCode = targetState == "Reconcile" ? JobReasonCode.ReconcileRequired : null;
        }

        // Cập nhật database
        if (_dbContextFactory != null)
        {
            await _dbLock.WaitAsync(cancellationToken);
            try
            {
                await using var db = _dbContextFactory();
                var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
                if (job != null)
                {
                    job.State = targetState == "Reconcile" ? JobState.AwaitingUser : JobState.Queued;
                    job.ReasonCode = targetState == "Reconcile" ? JobReasonCode.ReconcileRequired : null;
                    job.UpdatedAtUtc = DateTime.UtcNow.ToString("o");
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            finally
            {
                _dbLock.Release();
            }
        }

        return targetState;
    }

    private async Task UpdateProfileJobsStateAsync(
        string profileId,
        string newState,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        if (_profileJobs.TryGetValue(profileId, out var jobs))
        {
            foreach (var kvp in jobs)
            {
                kvp.Value.State = newState;
                if (kvp.Value.SideEffectSubmitted)
                {
                    kvp.Value.ReasonCode = JobReasonCode.ReconcileRequired;
                }
                else
                {
                    kvp.Value.ReasonCode = reasonCode;
                }
            }
        }

        if (_dbContextFactory != null)
        {
            await _dbLock.WaitAsync(cancellationToken);
            try
            {
                await using var db = _dbContextFactory();
                var boundJobIds = await db.ExecutionBindings
                    .Where(b => b.ProfileId == profileId)
                    .Select(b => b.JobId)
                    .ToListAsync(cancellationToken);

                if (boundJobIds.Count > 0)
                {
                    var dbJobs = await db.Jobs
                        .Where(j => boundJobIds.Contains(j.Id))
                        .ToListAsync(cancellationToken);

                    string nowIso = DateTime.UtcNow.ToString("o");
                    foreach (var job in dbJobs)
                    {
                        job.State = newState;
                        bool hasInFlightSideEffect = (job.SideEffectSubmitted == 1 && string.IsNullOrEmpty(job.ResultReference)) ||
                            (jobs != null && jobs.TryGetValue(job.Id, out var tInfo) && tInfo.SideEffectSubmitted);

                        job.ReasonCode = hasInFlightSideEffect ? JobReasonCode.ReconcileRequired : reasonCode;
                        job.UpdatedAtUtc = nowIso;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            finally
            {
                _dbLock.Release();
            }
        }
    }

    private void OnProcessControllerContextClosed(object? sender, string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;
        if (_programmaticClosing.TryGetValue(profileId, out var closing) && closing) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await HandleWindowClosedByXAsync(profileId);
            }
            catch
            {
                // Giữ lỗi nội bộ, không crash runtime
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_processController != null)
        {
            _processController.ContextClosed -= OnProcessControllerContextClosed;
        }

        _dbLock.Dispose();
    }
}
