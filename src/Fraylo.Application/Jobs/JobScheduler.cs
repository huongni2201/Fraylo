using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Jobs;

/// <summary>
/// Bộ điều phối phân phối công bằng (Fair Scheduler), thu hồi lease hết hạn (Lease Sweeper)
/// và bảo vệ hạn ngạch trình duyệt (Browser Tab Quota) theo kiến trúc T15.
/// </summary>
public sealed class JobScheduler : IJobWakeupNotifier, IAsyncDisposable
{
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly JobReservation _reservation;
    private readonly IJobExecutor _executor;
    private readonly IBrowserGatekeeper _gatekeeper;
    private readonly Func<DateTimeOffset>? _clock;
    private readonly JobCrashRecoveryService _crashRecoveryService;

    private readonly Channel<JobWakeupSignal> _wakeupChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _workerCancellations = new();
    private readonly ConcurrentDictionary<string, Task> _runningWorkerTasks = new();
    private Task? _schedulerLoopTask;
    private int _isDisposed;

    private readonly string _schedulerId = $"scheduler-{Guid.NewGuid():N}";
    private const int CandidatePageSize = 15;

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan FallbackPollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public IBrowserGatekeeper Gatekeeper => _gatekeeper;
    public ConcurrentDictionary<string, Task> RunningWorkerTasks => _runningWorkerTasks;
    public Exception? LastWorkerException { get; private set; }

    public event Action<JobStateChangedPayload>? OnJobStateChanged;
    public event Action<JobProgressUpdatedPayload>? OnJobProgressUpdated;

    public JobScheduler(
        DbContextOptions<FrayloDbContext> dbOptions,
        JobReservation reservation,
        IJobExecutor executor,
        IBrowserGatekeeper? gatekeeper = null,
        Func<DateTimeOffset>? clock = null)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _reservation = reservation ?? throw new ArgumentNullException(nameof(reservation));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _gatekeeper = gatekeeper ?? new DefaultBrowserGatekeeper();
        _clock = clock;
        _crashRecoveryService = new JobCrashRecoveryService(_dbOptions, _clock, this, payload =>
        {
            OnJobStateChanged?.Invoke(payload);
        });

        // Channel đánh thức dạng bounded, nếu đầy sẽ ghi đè signal cũ (DropOldest) để không bao giờ nghẽn bộ nhớ
        var channelOptions = new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        _wakeupChannel = Channel.CreateBounded<JobWakeupSignal>(channelOptions);
    }

    private DateTimeOffset UtcNow => _clock?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>
    /// Gửi tín hiệu đánh thức vào In-Memory Channel để Scheduler lập tức phân phối job mới.
    /// </summary>
    public void NotifyWakeup(JobWakeupSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        _wakeupChannel.Writer.TryWrite(signal);
    }

    /// <summary>
    /// Khởi động vòng lặp điều phối nền của Scheduler.
    /// </summary>
    public void Start()
    {
        if (_schedulerLoopTask is null)
        {
            _schedulerLoopTask = Task.Run(() => SchedulerLoopAsync(_cts.Token));
        }
    }

    /// <summary>
    /// Vòng lặp chính: Đợi tín hiệu Wakeup hoặc timeout FallbackPollInterval (chống starvation).
    /// </summary>
    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 1. Quét thu hồi lease và phục hồi sự cố định kỳ
                await RunCrashRecoverySweepAsync(cancellationToken).ConfigureAwait(false);

                // 2. Cố gắng phân phối các job sẵn sàng
                await DispatchPendingJobsAsync(cancellationToken).ConfigureAwait(false);

                // 3. Đợi tín hiệu đánh thức từ Channel hoặc timeout định kỳ
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(FallbackPollInterval);

                try
                {
                    await _wakeupChannel.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
                    while (_wakeupChannel.Reader.TryRead(out _)) { }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Hết timeout định kỳ: tiếp tục vòng lặp để Fallback Polling
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log lỗi và nghỉ ngắn tránh spinning loop khi gặp sự cố DB nghiêm trọng
                System.Diagnostics.Debug.WriteLine($"[JobScheduler] Lỗi trong scheduler loop: {ex.Message}");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Thực hiện điều phối tuần tự đúng 1 Job tiếp theo từ queue SQLite chung theo thứ tự FIFO (createdAtUtc rồi jobId).
    /// </summary>
    public async Task<bool> DispatchNextSingleJobAsync(CancellationToken cancellationToken = default)
    {
        return await TryDispatchNextJobAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Đợi tiến trình Worker hoàn tất thực thi tác vụ (nếu đang chạy) với khoảng thời gian timeout xác định.
    /// Trả về true nếu worker đã hoàn thành hoặc không còn task đang chạy.
    /// </summary>
    public async Task<bool> WaitForWorkerCompletionAsync(string jobId, TimeSpan timeout)
    {
        if (_runningWorkerTasks.TryGetValue(jobId, out var task))
        {
            var completedTask = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            return completedTask == task;
        }
        return true;
    }

    /// <summary>
    /// Thực hiện điều phối độc lập từ queue SQLite chung theo plan 1.2 §2.2.
    /// Chọn job đủ điều kiện cũ nhất (FIFO: createdAtUtc, jobId). Job chờ provider/quota không chặn job khác.
    /// </summary>
    public async Task<int> DispatchPendingJobsAsync(CancellationToken cancellationToken = default)
    {
        int totalDispatched = 0;
        while (await TryDispatchNextJobAsync(cancellationToken).ConfigureAwait(false))
        {
            totalDispatched++;
        }
        return totalDispatched;
    }

    /// <summary>
    /// Thử cấp quyền và khởi chạy 1 Job hợp lệ từ queue chung.
    /// Lấy job từ hàng đợi toàn ứng dụng; pause được quản lý theo Project workflow.
    /// </summary>
    private async Task<bool> TryDispatchNextJobAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = new FrayloDbContext(_dbOptions);
        var nowIso = UtcNow.ToString("o");

        // PV-R4: Lấy danh sách các dự án đang tạm dừng quy trình (runState == Paused) để loại trừ
        var pausedWorkflowProjectSettings = await dbContext.AppSettings
            .AsNoTracking()
            .Where(s => s.Key.StartsWith("project.workflow.config.") && s.Value.Contains("\"runState\":\"Paused\""))
            .Select(s => s.Key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var pausedWorkflowProjectIds = pausedWorkflowProjectSettings
            .Select(k => k.Substring("project.workflow.config.".Length))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Duyệt theo trang cho đến khi reserve được một job: job bị chặn đầu hàng không được che job hợp lệ phía sau
        for (var offset = 0; ; offset += CandidatePageSize)
        {
            var candidateJobs = await dbContext.Jobs
                .AsNoTracking()
                .Where(j => j.State == JobState.Queued &&
                            !pausedWorkflowProjectIds.Contains(j.ProjectId) &&
                            (j.RetryAfterUtc == null || string.Compare(j.RetryAfterUtc, nowIso) <= 0))
                .OrderBy(j => j.CreatedAtUtc)
                .ThenBy(j => j.Id)
                .Skip(offset)
                .Take(CandidatePageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (candidateJobs.Count == 0)
            {
                return false;
            }

            foreach (var candidate in candidateJobs)
            {
                // Thực hiện Atomic Reservation (BEGIN IMMEDIATE + CAS) với Slot Occupancy
                var reserveResult = await _reservation.TryReserveJobAsync(
                    candidate.Id,
                    _schedulerId,
                    LeaseDuration,
                    isProfileAvailable: profileId =>
                        !_gatekeeper.IsDispatchPaused(profileId) &&
                        !_gatekeeper.IsProfileBusyWithLogin(profileId) &&
                        !_gatekeeper.IsRoleActive(profileId, candidate.TaskType),
                    getOpenProfileIds: () => _gatekeeper.GetActiveOrBusyProfileIds(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (reserveResult.Acquired && reserveResult.Job is not null)
                {
                    var assignedProfileId = reserveResult.Binding?.ProfileId ?? "default_profile";
                    var assignedAccountId = reserveResult.Binding?.ProviderAccountId;

                    NotifyStateChanged(reserveResult.Job.Id, reserveResult.Job.ProjectId, JobState.Queued, JobState.Reserved, null, null, reserveResult.Job.Revision);

                    var workerJob = reserveResult.Job;
                    var workerTask = Task.Run(async () =>
                    {
                        try
                        {
                            await ExecuteWorkerAsync(workerJob, assignedProfileId, assignedAccountId).ConfigureAwait(false);
                        }
                        finally
                        {
                            _runningWorkerTasks.TryRemove(workerJob.Id, out _);
                        }
                    });

                    _runningWorkerTasks.TryAdd(workerJob.Id, workerTask);
                    return true;
                }
                // Nếu không reserve được do provider/capacity của job này, loop tiếp tục thử job kế tiếp (không chặn job khác!)
            }
        }
    }

    /// <summary>
    /// Quy trình quét 3 cấp độ (3-Tier Crash Recovery Sweep) phục hồi sự cố và thu hồi Dead Worker:
    /// 1. Ưu tiên 1 (Side-effect Guard): Job đã gửi side-effect -> Chuyển sang AwaitingUser (CẤM retry tự động!) và bảo tồn occupancy.
    /// 2. Ưu tiên 2 (Safe Retry): Job chưa gửi side-effect & còn lượt thử -> Chuyển về Queued kèm retry_after và giải phóng occupancy.
    /// 3. Ưu tiên 3 (Exceeded Attempts): Job chưa gửi side-effect & hết lượt -> Chuyển sang Failed và giải phóng occupancy.
    /// Ủy quyền cho JobCrashRecoveryService chuyên trách của Task T16.
    /// </summary>
    public async Task<int> RunCrashRecoverySweepAsync(CancellationToken cancellationToken = default)
    {
        var sweepResult = await _crashRecoveryService.PerformRecoverySweepAsync(cancellationToken).ConfigureAwait(false);
        return sweepResult.RecoveredCount;
    }

    /// <summary>
    /// Vòng đời thực thi tác vụ trên tiến trình Worker:
    /// Quản lý Tab Lease, Heartbeat định kỳ, cam kết cờ Side Effect và commit kết quả cuối cùng.
    /// </summary>
    private async Task ExecuteWorkerAsync(Job reservedJob, string profileId, string? providerAccountId = null)
    {
        string jobId = reservedJob.Id;
        long fencingToken = reservedJob.FencingToken;
        string role = reservedJob.TaskType;
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _workerCancellations[jobId] = workerCts;
        IDisposable? tabLease = null;
        Timer? heartbeatTimer = null;
        bool sideEffectCommitted = false;

        try
        {
            // 1. Chiếm giữ Tab Lease độc quyền cho role này TRƯỚC KHI chuyển sang Running và TRƯỚC KHI gọi executor
            tabLease = await _gatekeeper.TryAcquireTabLeaseAsync(profileId, role, jobId, workerCts.Token).ConfigureAwait(false);
            if (tabLease is null)
            {
                // Thất bại chiếm lease (do role tab đang bận):
                // Tuyệt đối KHÔNG thực thi executor! Hoàn trả job về Queued an toàn (không tăng attempts).
                await _reservation.ReleaseReservationDueToNoCapacityAsync(jobId, fencingToken, CancellationToken.None).ConfigureAwait(false);
                reservedJob.Revision += 1;
                NotifyStateChanged(jobId, reservedJob.ProjectId, JobState.Reserved, JobState.Queued, "tab_capacity_unavailable", null, reservedJob.Revision);
                return;
            }

            // Đăng ký theo dõi job với browser checkpoint gatekeeper
            _gatekeeper.TrackJob(profileId, jobId, sideEffectSubmitted: reservedJob.SideEffectSubmitted == 1, state: JobState.Running);

            // 2. Đã chiếm được lease -> Chuyển trạng thái từ Reserved sang Running
            await _reservation.TransitionToRunningAsync(jobId, fencingToken, workerCts.Token).ConfigureAwait(false);
            reservedJob.Revision += 1;
            NotifyStateChanged(jobId, reservedJob.ProjectId, JobState.Reserved, JobState.Running, null, null, reservedJob.Revision);

            // 3. Khởi tạo Heartbeat Timer gia hạn lease định kỳ mỗi HeartbeatInterval
            heartbeatTimer = new Timer(async _ =>
            {
                try
                {
                    await _reservation.SendHeartbeatAsync(
                        jobId,
                        _schedulerId,
                        fencingToken,
                        LeaseDuration,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (FencingTokenStaleException)
                {
                    // Bị loại bỏ bởi worker mới hơn (Zombie Worker) -> Hủy bỏ ngay lập tức tiến trình worker cũ
                    workerCts.Cancel();
                }
                catch
                {
                    // Bỏ qua lỗi tạm thời khi gửi heartbeat
                }
            }, null, HeartbeatInterval, HeartbeatInterval);

            // 4. Chuẩn bị ngữ cảnh thực thi (bao gồm checkpoint intermediate output nếu có từ lần chạy trước)
            var executionContext = new JobExecutionContext(
                JobId: reservedJob.Id,
                ProjectId: reservedJob.ProjectId,
                SceneId: reservedJob.SceneId,
                TaskType: reservedJob.TaskType,
                ProviderKey: reservedJob.ProviderKey,
                StorageRootId: reservedJob.StorageRootId,
                PromptSnapshotText: reservedJob.PromptSnapshotText,
                ConfigSnapshotJson: reservedJob.ConfigSnapshotJson,
                FencingToken: fencingToken,
                Attempts: reservedJob.Attempts,
                ProfileId: profileId,
                ProviderAccountId: providerAccountId,
                ExistingResultText: reservedJob.ResultText,
                ExistingResultReference: reservedJob.ResultReference
            );

            // 5. Thực thi qua JobExecutor
            var result = await _executor.ExecuteAsync(
                executionContext,
                onPreSideEffectCommitAsync: async () =>
                {
                    // CRITICAL BOUNDARY: Commit cờ side_effect_submitted = 1 TRƯỚC KHI web action click
                    await _reservation.CommitSideEffectAsync(jobId, fencingToken, workerCts.Token).ConfigureAwait(false);
                    sideEffectCommitted = true;
                    _gatekeeper.SetJobSideEffectSubmitted(profileId, jobId, true);
                },
                onProgressAsync: (percent, step) =>
                {
                    OnJobProgressUpdated?.Invoke(new JobProgressUpdatedPayload(
                        JobId: jobId,
                        ProjectId: reservedJob.ProjectId,
                        SceneId: reservedJob.SceneId,
                        State: JobState.Running,
                        ProgressPercent: percent,
                        CurrentStep: step,
                        JobRevision: reservedJob.Revision
                    ));
                    return Task.CompletedTask;
                },
                workerCts.Token).ConfigureAwait(false);

            // 6. Dừng Heartbeat Timer
            if (heartbeatTimer is not null)
            {
                await heartbeatTimer.DisposeAsync().ConfigureAwait(false);
                heartbeatTimer = null;
            }

            // 7. Commit kết quả thành công (hoặc muộn sau khi bị hủy)
            if (result.Success)
            {
                var commitResult = await _reservation.CommitSuccessAsync(
                    jobId,
                    fencingToken,
                    result.ResultReference,
                    result.ResultText,
                    workerCts.Token).ConfigureAwait(false);

                NotifyStateChanged(jobId, reservedJob.ProjectId, JobState.Running, commitResult.State, commitResult.ReasonCode, result.ResultReference, commitResult.Revision);
            }
            else
            {
                // T16 CHECKPOINT / FINALIZE:
                // Nếu result có chứa intermediate result (ví dụ text hoặc file đã sinh nhưng lưu storage thất bại),
                // checkpoint đệm ngay vào DB để lần retry sau chỉ finalize từ checkpoint mà không cần gọi lại AI!
                if (!string.IsNullOrWhiteSpace(result.ResultText) || !string.IsNullOrWhiteSpace(result.ResultReference))
                {
                    await using var dbContext = new FrayloDbContext(_dbOptions);
                    var nowIso = UtcNow.ToString("o");
                    await dbContext.Database.ExecuteSqlRawAsync(
                        @"UPDATE Job
                          SET revision = revision + 1,
                              result_text = COALESCE(@resText, result_text),
                              result_reference = COALESCE(@resRef, result_reference),
                              side_effect_submitted = 1,
                              updated_at_utc = @now
                          WHERE id = @id AND fencing_token = @token;",
                        new object[]
                        {
                            new SqliteParameter("@resText", (object?)result.ResultText ?? DBNull.Value),
                            new SqliteParameter("@resRef", (object?)result.ResultReference ?? DBNull.Value),
                            new SqliteParameter("@now", nowIso),
                            new SqliteParameter("@id", jobId),
                            new SqliteParameter("@token", fencingToken)
                        }).ConfigureAwait(false);
                }

                await HandleExecutionFailureAsync(reservedJob, fencingToken, result.ReasonCode ?? "execution_failed", sideEffectCommitted || result.SideEffectSubmitted).ConfigureAwait(false);
            }
        }
        catch (FencingTokenStaleException)
        {
            // Zombie worker bị fenced out: Tuyệt đối không can thiệp vào DB nữa
            System.Diagnostics.Debug.WriteLine($"[JobScheduler] Worker {jobId} bị fencing out (zombie). Dừng an toàn.");
        }
        catch (Exception ex)
        {
            LastWorkerException = ex;
            await HandleExecutionFailureAsync(reservedJob, fencingToken, ex.Message, sideEffectCommitted).ConfigureAwait(false);
        }
        finally
        {
            _workerCancellations.TryRemove(jobId, out _);
            if (heartbeatTimer is not null)
            {
                await heartbeatTimer.DisposeAsync().ConfigureAwait(false);
            }

            _gatekeeper.UntrackJob(profileId, jobId);
            tabLease?.Dispose();
            _gatekeeper.ReleaseTabLease(profileId, role, jobId);

            // v1.1 §1.3: Khi profile không còn tab/job bận nào khác (idle) thì đóng context an toàn
            if (_gatekeeper.IsProfileIdle(profileId))
            {
                _gatekeeper.CloseProfileIfIdle(profileId);
            }

            // Đánh thức Scheduler ngay lập tức để cấp slot rảnh cho job kế tiếp mà không cần đợi cả đợt
            NotifyWakeup(new JobWakeupSignal("WorkerCompleted", string.Empty, jobId));
        }
    }

    /// <summary>Dừng worker đang chạy của job (người dùng hủy): đóng tab và ngừng chờ provider.</summary>
    public void CancelRunningWorker(string jobId)
    {
        if (_workerCancellations.TryGetValue(jobId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleExecutionFailureAsync(Job job, long fencingToken, string reason, bool sideEffectCommitted)
    {
        try
        {
            // Chỉ render video đã gửi mới cần giữ ranh giới side effect (không gửi lại mù, khôi phục từ side chat);
            // Script/Image/VideoPrompt lỗi sau khi gửi chỉ là lỗi thường: xóa cờ để thử lại được, không có bước đối soát.
            var keepSideEffect = sideEffectCommitted && job.TaskType == JobTaskType.VideoRender;
            var decision = JobRetryAndCancelPolicy.EvaluateFailure(job, reason, keepSideEffect);

            if (decision.TargetState == JobState.AwaitingUser)
            {
                await using var dbContext = new FrayloDbContext(_dbOptions);
                var nowIso = UtcNow.ToString("o");

                // Chỉ ghi khi worker còn sở hữu job: job đã Canceled (hủy trước submit không đổi fencing token) giữ nguyên trạng thái
                var rows = await dbContext.Database.ExecuteSqlRawAsync(
                    @"UPDATE Job
                      SET revision = revision + 1,
                          state = 'AwaitingUser',
                          reason_code = @reason,
                          lease_expires_at_utc = NULL,
                          updated_at_utc = @now
                      WHERE id = @id AND fencing_token = @token AND state IN ('Reserved', 'Running');",
                    new object[]
                    {
                        new SqliteParameter("@reason", decision.FinalReasonCode),
                        new SqliteParameter("@now", nowIso),
                        new SqliteParameter("@id", job.Id),
                        new SqliteParameter("@token", fencingToken)
                    }).ConfigureAwait(false);
                if (rows == 0)
                {
                    return;
                }

                job.Revision += 1;
                NotifyStateChanged(job.Id, job.ProjectId, JobState.Running, JobState.AwaitingUser, decision.FinalReasonCode, null, job.Revision);
            }
            else if (decision.ShouldRollbackAttempts)
            {
                // Hạn ngạch tab/capacity: bảo toàn attempts
                await _reservation.ReleaseReservationDueToNoCapacityAsync(job.Id, fencingToken, CancellationToken.None).ConfigureAwait(false);
                job.Revision += 1;
                NotifyStateChanged(job.Id, job.ProjectId, JobState.Running, JobState.Queued, decision.FinalReasonCode, null, job.Revision);
            }
            else
            {
                bool canRetry = decision.CanRetry;
                var retryDelay = decision.RetryDelay;

                await _reservation.CommitFailureAsync(
                    job.Id,
                    fencingToken,
                    decision.FinalReasonCode,
                    canRetry,
                    retryDelay,
                    CancellationToken.None,
                    clearSideEffect: sideEffectCommitted && !keepSideEffect).ConfigureAwait(false);

                job.Revision += 1;
                var newState = canRetry ? JobState.Queued : JobState.Failed;
                NotifyStateChanged(job.Id, job.ProjectId, JobState.Running, newState, decision.FinalReasonCode, null, job.Revision);
            }
        }
        catch (FencingTokenStaleException)
        {
            // Fenced out, an toàn bỏ qua
        }
        catch (Exception ex)
        {
            LastWorkerException = ex;
            System.Diagnostics.Debug.WriteLine($"[JobScheduler] Lỗi khi xử lý execution failure: {ex.Message}");
        }
    }

    private void NotifyStateChanged(string jobId, string? projectId, string prevState, string newState, string? reasonCode, string? resultRef, long jobRevision = 0)
    {
        OnJobStateChanged?.Invoke(new JobStateChangedPayload(
            JobId: jobId,
            ProjectId: projectId,
            PreviousState: prevState,
            NewState: newState,
            ReasonCode: reasonCode,
            ResultReference: resultRef,
            TimestampUtc: UtcNow.ToString("o"),
            JobRevision: jobRevision
        ));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _wakeupChannel.Writer.TryComplete();

        if (_schedulerLoopTask is not null)
        {
            try
            {
                await _schedulerLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Bỏ qua khi cancel
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JobScheduler] Lỗi đợi loop: {ex.Message}");
            }
        }

        var runningTasks = _runningWorkerTasks.Values.ToArray();
        if (runningTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(runningTasks).WaitAsync(DrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                System.Diagnostics.Debug.WriteLine("[JobScheduler] Timeout khi drain active worker tasks.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JobScheduler] Lỗi khi drain workers: {ex.Message}");
            }
        }

        _cts.Dispose();
    }
}
