using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Jobs;

/// <summary>
/// Chi tiết kết quả phục hồi cho từng tác vụ trong đợt quét.
/// </summary>
public sealed record JobRecoveryItemResult(
    string JobId,
    string PreviousState,
    string NewState,
    string? ReasonCode,
    bool PreservedOccupancy,
    bool ReleasedOccupancy
);

/// <summary>
/// Kết quả tổng hợp của đợt quét phục hồi sự cố (Crash Recovery Sweep).
/// </summary>
public sealed record JobRecoverySweepResult(
    int TotalCandidates,
    int RecoveredCount,
    int RetriedCount,
    int MovedToReconcileCount,
    int FailedCount,
    IReadOnlyList<JobRecoveryItemResult> Items
);

/// <summary>
/// Dịch vụ phục hồi sự cố hàng đợi tác vụ (Job Crash Recovery Service):
/// Xử lý quét định kỳ (Periodic Sweep) và phục hồi khi khởi động (Startup Recovery).
/// Thu hồi các lease đã quá hạn, phân định ranh giới side-effect và bảo tồn slot occupancy khi cần đối soát.
/// </summary>
public sealed class JobCrashRecoveryService
{
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly Func<DateTimeOffset>? _clock;
    private readonly IJobWakeupNotifier? _wakeupNotifier;

    public event Action<JobStateChangedPayload>? OnJobStateChanged;

    public JobCrashRecoveryService(
        DbContextOptions<FrayloDbContext> dbOptions,
        Func<DateTimeOffset>? clock = null,
        IJobWakeupNotifier? wakeupNotifier = null,
        Action<JobStateChangedPayload>? onJobStateChanged = null)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _clock = clock;
        _wakeupNotifier = wakeupNotifier;
        if (onJobStateChanged != null)
        {
            OnJobStateChanged += onJobStateChanged;
        }
    }

    private DateTimeOffset UtcNow => _clock?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>
    /// Thực hiện quét định kỳ (Crash Recovery Sweep) thu hồi các tác vụ bị quá hạn lease (LeaseExpiresAtUtc < now).
    /// Tuân thủ quy tắc phục hồi 2 nhánh:
    /// - Nhánh 1 (SideEffectSubmitted == 0): Failed và giải phóng occupancy; người dùng chủ động thử lại.
    /// - Nhánh 2 (SideEffectSubmitted == 1): Chuyển AwaitingUser (reconcile_required), CẤM retry tự động và BẢO TỒN occupancy.
    /// Fencing Token và CAS Guard đảm bảo đúng 1 winner thành công trong các cuộc cạnh tranh đồng thời.
    /// </summary>
    public async Task<JobRecoverySweepResult> PerformRecoverySweepAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = new FrayloDbContext(_dbOptions);
        var nowIso = UtcNow.ToString("o");

        // Thu hồi lease quá hạn và các lượt tự retry còn lưu từ phiên bản trước.
        var expiredCandidates = await dbContext.Jobs
            .AsNoTracking()
            .Where(j => (j.State == JobState.Queued && j.RetryAfterUtc != null) ||
                       ((j.State == JobState.Reserved || j.State == JobState.Running) &&
                        j.LeaseExpiresAtUtc != null &&
                        string.Compare(j.LeaseExpiresAtUtc, nowIso) < 0))
            .Select(j => new { j.Id, j.FencingToken, j.LeaseExpiresAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (expiredCandidates.Count == 0)
        {
            return new JobRecoverySweepResult(0, 0, 0, 0, 0, Array.Empty<JobRecoveryItemResult>());
        }

        int recoveredCount = 0;
        int retriedCount = 0;
        int movedToReconcileCount = 0;
        int failedCount = 0;
        var itemResults = new List<JobRecoveryItemResult>();

        foreach (var candidate in expiredCandidates)
        {
            await using var txContext = new FrayloDbContext(_dbOptions);
            var dbConn = txContext.Database.GetDbConnection();
            if (dbConn.State != ConnectionState.Open)
            {
                await dbConn.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            IDbContextTransaction transaction;
            if (dbConn is SqliteConnection sqliteConn)
            {
                var sqliteTx = sqliteConn.BeginTransaction(deferred: false);
                transaction = await txContext.Database.UseTransactionAsync(sqliteTx, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Không thể tạo SqliteTransaction cho crash recovery sweep.");
            }
            else
            {
                transaction = await txContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            }

            await using (transaction)
            {
                var job = await txContext.Jobs
                    .FirstOrDefaultAsync(j => j.Id == candidate.Id, cancellationToken).ConfigureAwait(false);

                var pendingAutoRetry = job?.State == JobState.Queued && job.RetryAfterUtc != null;
                if (job == null || (!pendingAutoRetry && job.State != JobState.Reserved && job.State != JobState.Running))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // CAS Guard: So khớp fencing token và thời hạn lease thực tế
                if (job.FencingToken != candidate.FencingToken ||
                    (!pendingAutoRetry && (job.LeaseExpiresAtUtc == null ||
                    string.Compare(job.LeaseExpiresAtUtc, nowIso) >= 0)))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var prevState = job.State;
                string newState;
                string? reasonCode;
                bool preservedOccupancy = false;
                bool releasedOccupancy = false;

                if (job.SideEffectSubmitted == 1)
                {
                    // Trường hợp 2: Đã submit side-effect -> Tuyệt đối CẤM resend mù hoặc auto-retry!
                    // BẮT BUỘC chuyển sang AwaitingUser kèm reconcile_required
                    // BẢO TỒN OCCUPANCY: Giữ nguyên ActiveSlotOccupancy để bảo vệ tài nguyên tab/slot của account đó
                    newState = JobState.AwaitingUser;
                    reasonCode = JobReasonCode.ReconcileRequired;
                    job.State = newState;
                    job.ReasonCode = reasonCode;
                    job.LeaseExpiresAtUtc = null;
                    job.LeaseOwnerId = null;
                    job.Revision += 1;
                    job.UpdatedAtUtc = nowIso;

                    preservedOccupancy = true;
                    movedToReconcileCount++;
                }
                else
                {
                    // Lease hết hạn cũng dừng job, không tự mở browser để chạy lại.
                    newState = JobState.Failed;
                    reasonCode = job.Attempts >= job.MaxAttempts
                        ? JobReasonCode.LeaseExpiredMaxAttempts
                        : JobReasonCode.NetworkError;
                    job.State = newState;
                    job.ReasonCode = reasonCode;
                    job.LeaseExpiresAtUtc = null;
                    job.LeaseOwnerId = null;
                    job.RetryAfterUtc = null;
                    job.Revision += 1;
                    job.UpdatedAtUtc = nowIso;

                    // Giải phóng slot occupancy
                    var occ = await txContext.ActiveSlotOccupancies
                        .FirstOrDefaultAsync(o => o.JobId == job.Id, cancellationToken).ConfigureAwait(false);
                    if (occ != null)
                    {
                        txContext.ActiveSlotOccupancies.Remove(occ);
                        releasedOccupancy = true;
                    }

                    failedCount++;
                }

                await txContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                recoveredCount++;

                var itemResult = new JobRecoveryItemResult(
                    JobId: job.Id,
                    PreviousState: prevState,
                    NewState: newState,
                    ReasonCode: reasonCode,
                    PreservedOccupancy: preservedOccupancy,
                    ReleasedOccupancy: releasedOccupancy
                );
                itemResults.Add(itemResult);

                // CHỈ PHÁT EVENT VÀ NOTIFY SAU KHI TRANSACTION COMMIT THÀNH CÔNG!
                NotifyStateChanged(job.Id, job.ProjectId, prevState, newState, reasonCode, job.ResultReference, job.Revision);

                if (newState == JobState.Queued)
                {
                    _wakeupNotifier?.NotifyWakeup(new JobWakeupSignal("crash_recovery_retry", job.Id));
                }
            }
        }

        return new JobRecoverySweepResult(
            TotalCandidates: expiredCandidates.Count,
            RecoveredCount: recoveredCount,
            RetriedCount: retriedCount,
            MovedToReconcileCount: movedToReconcileCount,
            FailedCount: failedCount,
            Items: itemResults
        );
    }

    /// <summary>
    /// Thực hiện quét phục hồi khi khởi động ứng dụng (Startup Recovery).
    /// Quét toàn bộ các tác vụ bị gián đoạn do sự cố crash phiên làm việc trước.
    /// </summary>
    public async Task<JobRecoverySweepResult> PerformStartupRecoveryAsync(CancellationToken cancellationToken = default)
    {
        return await PerformRecoverySweepAsync(cancellationToken).ConfigureAwait(false);
    }

    private void NotifyStateChanged(
        string jobId,
        string? projectId,
        string prevState,
        string newState,
        string? reasonCode,
        string? resultRef,
        long jobRevision = 0)
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
}
