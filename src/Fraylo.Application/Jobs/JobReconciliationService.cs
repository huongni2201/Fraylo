using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Jobs;

/// <summary>
/// Dịch vụ đối soát tác vụ (Job Reconciliation Service):
/// Phục vụ đối soát và xử lý các tác vụ rơi vào trạng thái 'AwaitingUser' (do crash sau khi gửi side-effect,
/// mất kết nối không chắc chắn nhà cung cấp đã nhận hay chưa, hoặc chờ can thiệp thủ công).
/// Hỗ trợ các hành động: retry / resubmit, mark_succeeded / resolve, mark_failed, cancel.
/// </summary>
public sealed class JobReconciliationService
{
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly IJobWakeupNotifier? _wakeupNotifier;
    private readonly Func<DateTimeOffset>? _clock;

    public event Action<JobStateChangedPayload>? OnJobStateChanged;

    public JobReconciliationService(
        DbContextOptions<FrayloDbContext> dbOptions,
        IJobWakeupNotifier? wakeupNotifier = null,
        Func<DateTimeOffset>? clock = null,
        Action<JobStateChangedPayload>? onJobStateChanged = null)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _wakeupNotifier = wakeupNotifier;
        _clock = clock;
        if (onJobStateChanged != null)
        {
            OnJobStateChanged += onJobStateChanged;
        }
    }

    private DateTimeOffset UtcNow => _clock?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>
    /// Thực hiện đối soát tác vụ theo yêu cầu (job.reconcile).
    /// Áp dụng cho các tác vụ ở trạng thái 'AwaitingUser'.
    /// Ngăn chặn resend mù nếu đã có side-effect mà chưa có xác nhận chắc chắn nhà cung cấp chưa nhận.
    /// </summary>
    public async Task<JobReconcileResponseData> ReconcileAsync(
        JobReconcileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw new JobValidationException("Trường 'jobId' không được để trống.", nameof(request.JobId));
        }

        if (string.IsNullOrWhiteSpace(request.Action))
        {
            throw new JobValidationException("Trường 'action' không được để trống.", nameof(request.Action));
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var query = dbContext.Jobs.Where(j => j.Id == request.JobId);

        var job = await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            throw new JobNotFoundException(request.JobId);
        }

        var hasOccupancy = await dbContext.ActiveSlotOccupancies
            .AnyAsync(o => o.JobId == job.Id, cancellationToken).ConfigureAwait(false);

        var isUnresolvedTerminalJob = (job.State == JobState.Canceled || job.State == JobState.Failed) && hasOccupancy;

        if (job.State != JobState.AwaitingUser && !isUnresolvedTerminalJob)
        {
            throw new JobConcurrencyConflictException(
                $"Tác vụ hiện ở trạng thái '{job.State}', không thể thực hiện đối soát (yêu cầu trạng thái 'AwaitingUser').",
                request.JobId);
        }

        var prevState = job.State;
        var nowIso = UtcNow.ToString("o");
        string currentState;
        string reconcileResult;
        string? reasonCode = null;

        var normalizedAction = request.Action.Trim().ToLowerInvariant();

        switch (normalizedAction)
        {
            case "retry":
            case "resubmit":
            case "finalize":
                // 1. retry / resubmit / finalize:
                // Nếu tác vụ đã ở trạng thái Canceled hoặc có CancelRequested == 1:
                // TUYỆT ĐỐI CHẶN retry / resubmit generation vì không được phép làm sống lại tác vụ đã hủy thành lần sinh mới.
                // Nếu là finalize trên tác vụ đã hủy: chỉ cho phép khi ĐÃ CÓ checkpoint trung gian (thu hồi/lưu output unlinked, không sinh lại).
                bool wasCanceled = prevState == JobState.Canceled || job.CancelRequested == 1 || job.State == JobState.Canceled;
                if ((normalizedAction == "retry" || normalizedAction == "resubmit") && wasCanceled)
                {
                    throw new JobValidationException(
                        "Không thể thử lại (retry) hoặc gửi lại (resubmit) tác vụ đã bị hủy bỏ bởi người dùng. Tác vụ đã hủy không được phép tạo lần sinh mới.",
                        nameof(request.Action));
                }

                // Nếu đã có checkpoint từ AI generation trước đó (ResultText hoặc ResultReference):
                // Đây là lượt hoàn tất lưu trữ (finalize) kết quả sau lỗi storage, KHÔNG resend AI nên KHÔNG cần ConfirmedNotReceived.
                // Nếu CHƯA có checkpoint mà SideEffectSubmitted == 1: BẮT BUỘC ConfirmedNotReceived == true để tránh resend mù!
                // ResultReference còn có thể là URL hội thoại sau lỗi sau khi gửi: chỉ Asset đã lưu mới là output để finalize.
                var hasOutputAsset = !string.IsNullOrWhiteSpace(job.ResultReference) &&
                    await dbContext.Assets.AnyAsync(a => a.Id == job.ResultReference, cancellationToken).ConfigureAwait(false);
                bool hasCheckpoint = !string.IsNullOrWhiteSpace(job.ResultText) || hasOutputAsset;

                if (normalizedAction == "finalize" && !hasCheckpoint)
                {
                    throw new JobValidationException(
                        wasCanceled
                            ? "Không thể finalize tác vụ đã bị hủy bỏ khi không có checkpoint kết quả. Tác vụ đã hủy không được phép sinh lại nội dung."
                            : "Không thể finalize tác vụ khi chưa có checkpoint kết quả trung gian.",
                        nameof(request.Action));
                }

                if (job.SideEffectSubmitted == 1 && !hasCheckpoint && request.ConfirmedNotReceived != true)
                {
                    throw new JobValidationException(
                        "Không thể gửi lại tác vụ đã phát sinh tác dụng phụ (side-effect) ra ngoài khi chưa có bằng chứng xác nhận chắc chắn nhà cung cấp chưa tiếp nhận (ConfirmedNotReceived == true). Vui lòng đối soát hoặc nhập kết quả thủ công.",
                        nameof(request.Action));
                }

                if (job.Attempts >= job.MaxAttempts && !hasCheckpoint)
                {
                    throw new JobValidationException(
                        $"Tác vụ đã vượt quá số lần thử tối đa ({job.Attempts}/{job.MaxAttempts}). Vui lòng tạo tác vụ mới.",
                        nameof(job.Attempts));
                }

                // Reset SideEffectSubmitted = 0 nếu không có checkpoint (resubmit từ đầu).
                // Nếu đã có checkpoint, giữ nguyên SideEffectSubmitted = 1 và ResultText / ResultReference để executor tiếp tục finalize!
                job.State = JobState.Queued;
                if (!hasCheckpoint)
                {
                    job.SideEffectSubmitted = 0;
                    job.SideEffectAtUtc = null;
                }

                if (wasCanceled)
                {
                    job.CancelRequested = 1;
                    job.ReasonCode = JobReasonCode.UserCanceled;
                    currentState = JobState.Queued;
                    reconcileResult = "Tác vụ đã hủy được đưa vào hàng đợi để thu hồi và lưu trữ kết quả checkpoint dưới dạng Unlinked mà không sinh lại nội dung.";
                }
                else
                {
                    job.ReasonCode = null;
                    currentState = JobState.Queued;
                    reconcileResult = hasCheckpoint
                        ? "Tác vụ đã được phục hồi từ checkpoint để hoàn tất lưu trữ (finalize) kết quả."
                        : "Tác vụ đã được xác minh an toàn và đưa về hàng đợi để gửi lại.";
                }

                job.LeaseExpiresAtUtc = null;
                job.LeaseOwnerId = null;
                job.RetryAfterUtc = null;
                job.Revision += 1;
                job.UpdatedAtUtc = nowIso;

                _wakeupNotifier?.NotifyWakeup(new JobWakeupSignal("job.reconcile.retry", string.Empty, job.Id));
                break;

            case "mark_succeeded":
            case "resolve":
                // 2. mark_succeeded / resolve:
                // BẮT BUỘC phải có ResultReference hoặc ResultText hợp lệ (không cho phép rỗng).
                var validReference = !string.IsNullOrWhiteSpace(request.ResultReference) ? request.ResultReference.Trim() : job.ResultReference;
                var validText = !string.IsNullOrWhiteSpace(request.ResultText) ? request.ResultText.Trim() : job.ResultText;

                if (string.IsNullOrWhiteSpace(validReference) && string.IsNullOrWhiteSpace(validText))
                {
                    throw new JobValidationException(
                        "Không thể đánh dấu tác vụ thành công (mark_succeeded) khi chưa có kết quả hợp lệ (ResultReference hoặc ResultText). Vui lòng cung cấp kết quả hoặc nhập thủ công.",
                        nameof(request.Action));
                }

                if (!string.IsNullOrWhiteSpace(request.ResultReference))
                {
                    job.ResultReference = request.ResultReference.Trim();
                }
                if (!string.IsNullOrWhiteSpace(request.ResultText))
                {
                    job.ResultText = request.ResultText.Trim();
                }

                bool wasCanceledOrCancelRequested = prevState == JobState.Canceled || job.CancelRequested == 1 || job.State == JobState.Canceled;

                if (wasCanceledOrCancelRequested)
                {
                    // v1.1 §3.3 & Finding 2: Tác vụ đã bị hủy -> Nhận kết quả muộn giữ nguyên trạng thái Canceled!
                    // Tuyệt đối không chuyển sang Succeeded, không tự áp kết quả vào Scene.
                    // Nếu kết quả tạo Asset (hoặc có Asset gắn với Job này), chuyển Asset sang "Unlinked".
                    job.State = JobState.Canceled;
                    job.CancelRequested = 1;
                    job.ReasonCode = JobReasonCode.UserCanceled;
                    job.LeaseExpiresAtUtc = null;
                    job.LeaseOwnerId = null;
                    job.Revision += 1;
                    job.UpdatedAtUtc = nowIso;

                    currentState = JobState.Canceled;
                    reasonCode = JobReasonCode.UserCanceled;
                    reconcileResult = "Kết quả muộn của tác vụ đã hủy được ghi nhận an toàn (Asset Unlinked), tác vụ giữ nguyên trạng thái Canceled và giải phóng slot occupancy.";

                    if (!string.IsNullOrWhiteSpace(job.ResultReference))
                    {
                        var asset = await dbContext.Assets
                            .FirstOrDefaultAsync(a => a.Id == job.ResultReference, cancellationToken).ConfigureAwait(false);
                        if (asset != null)
                        {
                            asset.Status = "Unlinked";
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(job.SceneId) && !string.IsNullOrWhiteSpace(job.ResultReference))
                    {
                        var scene = await dbContext.Scenes
                            .FirstOrDefaultAsync(s => s.Id == job.SceneId, cancellationToken).ConfigureAwait(false);
                        if (scene != null)
                        {
                            if (scene.ImageAssetId == job.ResultReference) scene.ImageAssetId = null;
                            if (scene.AudioAssetId == job.ResultReference) scene.AudioAssetId = null;
                            if (scene.VideoAssetId == job.ResultReference) scene.VideoAssetId = null;
                        }
                    }
                }
                else
                {
                    job.State = JobState.Succeeded;
                    job.ReasonCode = null;
                    job.LeaseExpiresAtUtc = null;
                    job.LeaseOwnerId = null;
                    job.Revision += 1;
                    job.UpdatedAtUtc = nowIso;

                    currentState = JobState.Succeeded;
                    reasonCode = null;
                    reconcileResult = "Tác vụ đã được xác nhận hoàn tất thành công với kết quả hợp lệ.";
                }

                // Giải phóng ActiveSlotOccupancy vì kết quả đã được đối soát an toàn
                var occSucceeded = await dbContext.ActiveSlotOccupancies
                    .FirstOrDefaultAsync(o => o.JobId == job.Id, cancellationToken).ConfigureAwait(false);
                if (occSucceeded != null)
                {
                    dbContext.ActiveSlotOccupancies.Remove(occSucceeded);
                }
                break;

            case "mark_failed":
                // 3. mark_failed:
                // Chuyển sang Failed với reason_code = 'reconcile_rejected'.
                // Nếu có unresolved side-effect (SideEffectSubmitted == 1 và chưa có kết quả):
                // chỉ giải phóng ActiveSlotOccupancy KHI ConfirmedNotReceived == true.
                // Nếu ConfirmedNotReceived != true: giữ nguyên ActiveSlotOccupancy trong DB để bảo vệ capacity!
                reasonCode = JobReasonCode.ReconcileRejected;
                job.State = JobState.Failed;
                job.ReasonCode = reasonCode;
                job.LeaseExpiresAtUtc = null;
                job.LeaseOwnerId = null;
                job.Revision += 1;
                job.UpdatedAtUtc = nowIso;

                currentState = JobState.Failed;

                var isUnresolvedSideEffectFailed = job.SideEffectSubmitted == 1
                    && string.IsNullOrWhiteSpace(job.ResultReference)
                    && string.IsNullOrWhiteSpace(job.ResultText);

                if (isUnresolvedSideEffectFailed && request.ConfirmedNotReceived != true)
                {
                    reconcileResult = "Tác vụ đã được đánh dấu thất bại sau đối soát nhưng slot occupancy vẫn được giữ nguyên do chưa xác nhận an toàn từ nhà cung cấp (cần ConfirmedNotReceived = true).";
                }
                else
                {
                    var occFailed = await dbContext.ActiveSlotOccupancies
                        .FirstOrDefaultAsync(o => o.JobId == job.Id, cancellationToken).ConfigureAwait(false);
                    if (occFailed != null)
                    {
                        dbContext.ActiveSlotOccupancies.Remove(occFailed);
                    }
                    if (request.ConfirmedNotReceived == true)
                    {
                        job.SideEffectSubmitted = 0;
                        job.SideEffectAtUtc = null;
                    }
                    reconcileResult = "Tác vụ đã được đánh dấu thất bại sau đối soát và giải phóng slot occupancy.";
                }
                break;

            case "cancel":
                // 4. cancel:
                // Chuyển sang Canceled với reason_code = 'user_canceled'.
                // Nếu có kết quả muộn đi kèm hoặc asset trong DB: chuyển Asset sang Unlinked, không gắn vào Scene.
                reasonCode = JobReasonCode.UserCanceled;
                job.State = JobState.Canceled;
                job.CancelRequested = 1;
                job.ReasonCode = reasonCode;
                job.LeaseExpiresAtUtc = null;
                job.LeaseOwnerId = null;
                job.Revision += 1;
                job.UpdatedAtUtc = nowIso;

                currentState = JobState.Canceled;

                if (!string.IsNullOrWhiteSpace(request.ResultReference))
                {
                    job.ResultReference = request.ResultReference.Trim();
                }
                if (!string.IsNullOrWhiteSpace(request.ResultText))
                {
                    job.ResultText = request.ResultText.Trim();
                }

                if (!string.IsNullOrWhiteSpace(job.ResultReference))
                {
                    var asset = await dbContext.Assets
                        .FirstOrDefaultAsync(a => a.Id == job.ResultReference, cancellationToken).ConfigureAwait(false);
                    if (asset != null)
                    {
                        asset.Status = "Unlinked";
                    }
                }

                if (!string.IsNullOrWhiteSpace(job.SceneId) && !string.IsNullOrWhiteSpace(job.ResultReference))
                {
                    var scene = await dbContext.Scenes
                        .FirstOrDefaultAsync(s => s.Id == job.SceneId, cancellationToken).ConfigureAwait(false);
                    if (scene != null)
                    {
                        if (scene.ImageAssetId == job.ResultReference) scene.ImageAssetId = null;
                        if (scene.AudioAssetId == job.ResultReference) scene.AudioAssetId = null;
                        if (scene.VideoAssetId == job.ResultReference) scene.VideoAssetId = null;
                    }
                }

                var isUnresolvedSideEffectCancel = job.SideEffectSubmitted == 1
                    && string.IsNullOrWhiteSpace(job.ResultReference)
                    && string.IsNullOrWhiteSpace(job.ResultText);

                if (isUnresolvedSideEffectCancel && request.ConfirmedNotReceived != true)
                {
                    reconcileResult = "Tác vụ đã được hủy bỏ bởi người dùng nhưng slot occupancy vẫn được giữ nguyên do chưa xác nhận an toàn từ nhà cung cấp (cần ConfirmedNotReceived = true).";
                }
                else
                {
                    var occCanceled = await dbContext.ActiveSlotOccupancies
                        .FirstOrDefaultAsync(o => o.JobId == job.Id, cancellationToken).ConfigureAwait(false);
                    if (occCanceled != null)
                    {
                        dbContext.ActiveSlotOccupancies.Remove(occCanceled);
                    }
                    if (request.ConfirmedNotReceived == true)
                    {
                        job.SideEffectSubmitted = 0;
                        job.SideEffectAtUtc = null;
                    }
                    reconcileResult = "Tác vụ đã được hủy bỏ bởi người dùng và giải phóng slot occupancy an toàn.";
                }
                break;

            default:
                throw new JobValidationException(
                    $"Hành động đối soát '{request.Action}' không hợp lệ. Các hành động hỗ trợ: retry, resubmit, finalize, mark_succeeded, resolve, mark_failed, cancel.",
                    nameof(request.Action));
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        OnJobStateChanged?.Invoke(new JobStateChangedPayload(
            JobId: job.Id,
            ProjectId: job.ProjectId,
            PreviousState: prevState,
            NewState: currentState,
            ReasonCode: reasonCode,
            ResultReference: job.ResultReference,
            TimestampUtc: nowIso,
            JobRevision: job.Revision
        ));

        return new JobReconcileResponseData(
            JobId: job.Id,
            PreviousState: prevState,
            CurrentState: currentState,
            ReconcileResult: reconcileResult,
            ResultReference: job.ResultReference,
            Revision: job.Revision
        );
    }
}
