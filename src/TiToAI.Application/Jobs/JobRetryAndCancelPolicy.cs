using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TiToAI.Contracts.Jobs;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Application.Jobs;

/// <summary>
/// Phân loại nguyên nhân thất bại của tác vụ để định tuyến xử lý chính xác theo execution-contract-v1.1.
/// </summary>
public enum FailureClassification
{
    /// <summary>
    /// Lỗi tạm thời, cho phép tự động thử lại (exponential backoff).
    /// </summary>
    Retryable,

    /// <summary>
    /// Lỗi cần người dùng can thiệp (auth, tài khoản, đối soát side effect, lưu trữ root).
    /// </summary>
    RequiresUserIntervention,

    /// <summary>
    /// Lỗi vĩnh viễn, vượt quá số lần thử tối đa hoặc cấu hình bất khả thi.
    /// </summary>
    PermanentFailure,

    /// <summary>
    /// Hết hạn ngạch tạm thời (tab lease / global budget), không tiêu hao lượt thử.
    /// </summary>
    CapacityExceeded
}

/// <summary>
/// Kết quả đánh giá quyết định retry/fail của tác vụ.
/// </summary>
public sealed record JobRetryDecision(
    bool CanRetry,
    string TargetState,
    string FinalReasonCode,
    TimeSpan? RetryDelay,
    bool ShouldRollbackAttempts,
    FailureClassification Classification
);

/// <summary>
/// Kết quả xử lý yêu cầu hủy tác vụ (Cancel Request).
/// </summary>
public sealed record CancellationHandlingResult(
    string JobId,
    string FinalState,
    bool CancelRequested,
    bool OccupancyReleased,
    string Message
);

/// <summary>
/// Kết quả xử lý thu hồi kết quả đến muộn (Late Result) trên tác vụ đã bị yêu cầu hủy.
/// </summary>
public sealed record LateResultHandlingResult(
    string JobId,
    string FinalState,
    string? ResultReference,
    string? ResultText,
    bool OccupancyReleased
);

/// <summary>
/// Chính sách xử lý Retry, Exponential Backoff, Cancellation và Zombie Worker Late Result
/// cho Durable Job Queue (Task T16) theo execution-contract-v1.1 và QUEUE_EXECUTION_SETTINGS_PLAN.md.
/// </summary>
public static class JobRetryAndCancelPolicy
{
    /// <summary>
    /// Thời gian chờ cơ sở cho exponential backoff (giây).
    /// </summary>
    public const int BaseBackoffSeconds = 5;

    /// <summary>
    /// Thời gian chờ tối đa cho exponential backoff (mặc định 5 phút = 300 giây).
    /// </summary>
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tính toán khoảng thời gian chờ (delay) trước khi thử lại tác vụ với Exponential Backoff hữu hạn.
    /// Công thức: 2^attempts * 5 giây, bị chặn trên bởi maxBackoff (mặc định 300s).
    /// Ví dụ:
    /// - attempts = 0 => 5s
    /// - attempts = 1 => 10s
    /// - attempts = 2 => 20s
    /// - attempts = 3 => 40s
    /// </summary>
    public static TimeSpan CalculateBackoff(int attempts, TimeSpan? maxBackoff = null)
    {
        int safeAttempts = Math.Max(0, attempts);
        double seconds = Math.Pow(2, safeAttempts) * BaseBackoffSeconds;
        var cap = maxBackoff ?? DefaultMaxBackoff;
        double cappedSeconds = Math.Min(seconds, cap.TotalSeconds);
        return TimeSpan.FromSeconds(cappedSeconds);
    }

    /// <summary>
    /// Lý do render video bị dừng ở bước chờ/tải sau khi đã gửi (có side chat đã lưu): job chuyển Failed và thử lại được từ bước đó.
    /// </summary>
    public static bool IsResumableRenderReason(string? reasonCode) =>
        string.Equals(reasonCode, JobReasonCode.RenderTimeout, StringComparison.OrdinalIgnoreCase)
        || string.Equals(reasonCode, JobReasonCode.RenderInterrupted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Kiểm tra xem mã lý do có thuộc nhóm nghẽn hạn ngạch (capacity / quota / budget) hay không.
    /// Khi gặp các mã này: BẢO TOÀN ATTEMPTS — tuyệt đối không tiêu hao lượt thử của tác vụ.
    /// </summary>
    public static bool ShouldRollbackAttemptsOnCapacityExceeded(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return false;
        }

        return string.Equals(reasonCode, JobReasonCode.TabCapacityUnavailable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, JobReasonCode.WaitingForCapacity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, JobReasonCode.WaitingForReconcile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, JobReasonCode.GlobalJobBudgetReached, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Job lỗi chỉ thử lại theo yêu cầu của người dùng.
    /// </summary>
    public static bool CanRetry(Job job, string? reasonCode, bool sideEffectSubmitted) => false;

    /// <summary>
    /// Kiểm tra xem lý do lỗi có bắt buộc người dùng can thiệp hay không.
    /// Bao gồm: auth/session (login_required, session_expired, auth_closed),
    /// sai tài khoản (wrong_account), thiếu thư mục lưu trữ (storage_root_unavailable),
    /// Adapter/capability chưa sẵn sàng được phân loại riêng để không chuyển giao thủ công.
    /// </summary>
    public static bool IsUserInterventionRequired(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return false;
        }

        return string.Equals(reasonCode, JobReasonCode.LoginRequired, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, JobReasonCode.SessionExpired, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, JobReasonCode.AuthClosed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, JobReasonCode.WrongAccount, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, JobReasonCode.StorageRootUnavailable, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAutomationUnavailable(string? reasonCode) =>
        string.Equals(reasonCode, "automation_blocked", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reasonCode, "adapter_not_available", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reasonCode, "missing_provider", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reasonCode, "CAPABILITY_UNSUPPORTED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reasonCode, "manual_input_required", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reasonCode, "MANUAL_HANDOFF_REQUIRED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Đánh giá toàn diện kết quả thực thi thất bại, phân loại hành động và tính toán trạng thái đích.
    /// </summary>
    public static JobRetryDecision EvaluateFailure(Job job, string? reasonCode, bool sideEffectSubmitted)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Render bị timeout hoặc gián đoạn: giải phóng tài nguyên, đưa Failed để người dùng thử lại từ bước thất bại.
        // Sau khi gửi prompt, retry chỉ tiếp tục chờ side chat đã lưu (xem RetryAsync), không gửi lại.
        if (IsResumableRenderReason(reasonCode))
        {
            return new JobRetryDecision(
                CanRetry: false,
                TargetState: JobState.Failed,
                FinalReasonCode: JobReasonCode.RenderTimeout,
                RetryDelay: null,
                ShouldRollbackAttempts: false,
                Classification: FailureClassification.PermanentFailure
            );
        }

        // 1. Nếu đã submit side effect: TUYỆT ĐỐI CẤM retry tự động, bắt buộc sang AwaitingUser để đối soát
        if (sideEffectSubmitted || job.SideEffectSubmitted == 1)
        {
            return new JobRetryDecision(
                CanRetry: false,
                TargetState: JobState.AwaitingUser,
                FinalReasonCode: JobReasonCode.ReconcileRequired,
                RetryDelay: null,
                ShouldRollbackAttempts: false,
                Classification: FailureClassification.RequiresUserIntervention
            );
        }

        // Automation chưa sẵn sàng: dừng rõ lý do, không retry hoặc yêu cầu nhập kết quả thủ công.
        if (IsAutomationUnavailable(reasonCode))
        {
            return new JobRetryDecision(
                CanRetry: false,
                TargetState: JobState.Failed,
                FinalReasonCode: reasonCode!,
                RetryDelay: null,
                ShouldRollbackAttempts: false,
                Classification: FailureClassification.PermanentFailure
            );
        }

        // 2. Lỗi cần người dùng can thiệp (Auth, Session, Wrong Account, Storage Root)
        if (IsUserInterventionRequired(reasonCode))
        {
            return new JobRetryDecision(
                CanRetry: false,
                TargetState: JobState.AwaitingUser,
                FinalReasonCode: reasonCode ?? JobReasonCode.LoginRequired,
                RetryDelay: null,
                ShouldRollbackAttempts: false,
                Classification: FailureClassification.RequiresUserIntervention
            );
        }

        // 3. Nghẽn hạn ngạch tạm thời (Tab lease chưa có, Global budget đầy)
        if (ShouldRollbackAttemptsOnCapacityExceeded(reasonCode))
        {
            return new JobRetryDecision(
                CanRetry: true,
                TargetState: JobState.Queued,
                FinalReasonCode: reasonCode!,
                RetryDelay: null,
                ShouldRollbackAttempts: true,
                Classification: FailureClassification.CapacityExceeded
            );
        }

        // 4. Hết lượt thử (Attempts >= MaxAttempts) -> Chuyển sang Failed vĩnh viễn
        if (job.Attempts >= job.MaxAttempts)
        {
            string finalReason = (string.Equals(reasonCode, JobReasonCode.LeaseExpiredRetry, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(reasonCode, JobReasonCode.LeaseExpiredMaxAttempts, StringComparison.OrdinalIgnoreCase))
                                 ? JobReasonCode.LeaseExpiredMaxAttempts
                                 : JobReasonCode.MaxAttemptsExceeded;

            return new JobRetryDecision(
                CanRetry: false,
                TargetState: JobState.Failed,
                FinalReasonCode: finalReason,
                RetryDelay: null,
                ShouldRollbackAttempts: false,
                Classification: FailureClassification.PermanentFailure
            );
        }

        // Job lỗi dừng ngay; giữ mã lỗi gốc để người dùng xem và chủ động thử lại.
        return new JobRetryDecision(
            CanRetry: false,
            TargetState: JobState.Failed,
            FinalReasonCode: reasonCode ?? JobReasonCode.NetworkError,
            RetryDelay: null,
            ShouldRollbackAttempts: false,
            Classification: FailureClassification.PermanentFailure
        );
    }

    /// <summary>
    /// Áp dụng chuyển trạng thái thử lại (Retry) lên thực thể Job.
    /// BẢO TOÀN BẤT BIẾN: Giữ nguyên ExecutionBinding (ProviderAccountId, ProfileId, TabSlotId)
    /// và snapshot input (ProviderKey, ConfigSnapshotJson, PromptSnapshotText, StorageRootId).
    /// </summary>
    public static void ApplyRetryToJob(Job job, TimeSpan retryDelay, string reasonCode, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        job.State = JobState.Queued;
        job.ReasonCode = reasonCode;
        job.LeaseExpiresAtUtc = null;
        job.LeaseOwnerId = null;
        job.RetryAfterUtc = now.Add(retryDelay).ToString("o");
        job.UpdatedAtUtc = now.ToString("o");

        // Ghi chú bảo toàn: Tuyệt đối không xóa hoặc thay đổi job.ExecutionBinding,
        // job.ProviderKey, job.StorageRootId, job.PromptSnapshotText, job.ConfigSnapshotJson.
    }

    /// <summary>
    /// Xử lý yêu cầu hủy tác vụ (Cancellation Request) tuân thủ 2 mốc thời gian:
    /// 1. Trước submit (SideEffectSubmitted == 0): Chuyển ngay sang Canceled, xóa slot occupancy.
    /// 2. Sau submit (SideEffectSubmitted == 1): Đánh dấu CancelRequested = 1, VẪN GIỮ slot occupancy để worker thu hồi kết quả hoặc đối soát an toàn.
    /// </summary>
    public static async Task<CancellationHandlingResult> HandleCancellationRequestAsync(
        TiToAIDbContext dbContext,
        string jobId,
        CancellationToken cancellationToken = default,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var job = await dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            throw new JobNotFoundException(jobId);
        }

        if (JobState.IsTerminal(job.State))
        {
            throw new JobConcurrencyConflictException($"Tác vụ '{jobId}' đã kết thúc ở trạng thái '{job.State}', không thể hủy bỏ.", jobId);
        }

        var nowIso = (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToString("o");

        // T16 FINDING 2: Kiểm tra tác dụng phụ chưa giải quyết (unresolved side effect):
        // Đã gửi side-effect ra ngoài (SideEffectSubmitted == 1) nhưng chưa có kết quả (ResultReference và ResultText đều trống).
        // TUYỆT ĐỐI CẤM giải phóng ActiveSlotOccupancy trong trường hợp này để bảo vệ capacity cho đến khi late result đến hoặc đối soát an toàn!
        bool hasUnresolvedSideEffect = job.SideEffectSubmitted == 1 &&
                                       string.IsNullOrWhiteSpace(job.ResultReference) &&
                                       string.IsNullOrWhiteSpace(job.ResultText);

        // Trường hợp 1: Job đang ở trạng thái AwaitingUser
        if (job.State == JobState.AwaitingUser)
        {
            job.State = JobState.Canceled;
            job.ReasonCode = JobReasonCode.UserCanceled;
            job.LeaseExpiresAtUtc = null;
            job.LeaseOwnerId = null;
            job.UpdatedAtUtc = nowIso;

            bool occupancyReleased = false;
            if (!hasUnresolvedSideEffect)
            {
                // Chỉ giải phóng slot occupancy khi KHÔNG có tác dụng phụ chưa giải quyết
                var occupancy = await dbContext.ActiveSlotOccupancies
                    .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken)
                    .ConfigureAwait(false);

                if (occupancy is not null)
                {
                    dbContext.ActiveSlotOccupancies.Remove(occupancy);
                    occupancyReleased = true;
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new CancellationHandlingResult(
                JobId: jobId,
                FinalState: JobState.Canceled,
                CancelRequested: false,
                OccupancyReleased: occupancyReleased,
                Message: hasUnresolvedSideEffect
                    ? "Tác vụ đang chờ đối soát đã được người dùng hủy bỏ. Do có tác dụng phụ chưa giải quyết, slot occupancy được bảo lưu cho đến khi đối soát an toàn."
                    : "Tác vụ đang chờ đối soát đã được người dùng hủy bỏ."
            );
        }

        // Trường hợp 2: Hủy TRƯỚC submit (SideEffectSubmitted == 0) hoặc job đã có kết quả hợp lệ
        if (job.SideEffectSubmitted == 0 || !hasUnresolvedSideEffect)
        {
            job.State = JobState.Canceled;
            job.ReasonCode = JobReasonCode.UserCanceled;
            job.LeaseExpiresAtUtc = null;
            job.LeaseOwnerId = null;
            job.UpdatedAtUtc = nowIso;

            // Giải phóng slot occupancy (nếu có)
            bool occupancyReleased = false;
            var occupancy = await dbContext.ActiveSlotOccupancies
                .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken)
                .ConfigureAwait(false);

            if (occupancy is not null)
            {
                dbContext.ActiveSlotOccupancies.Remove(occupancy);
                occupancyReleased = true;
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new CancellationHandlingResult(
                JobId: jobId,
                FinalState: JobState.Canceled,
                CancelRequested: false,
                OccupancyReleased: occupancyReleased,
                Message: job.SideEffectSubmitted == 0
                    ? "Tác vụ đã được hủy bỏ thành công trước khi gửi side-effect."
                    : "Tác vụ đã có kết quả hợp lệ đã được hủy bỏ và giải phóng slot occupancy an toàn."
            );
        }

        // Trường hợp 3: Hủy SAU submit (SideEffectSubmitted == 1, job đang Reserved/Running và chưa có kết quả)
        job.CancelRequested = 1;
        job.UpdatedAtUtc = nowIso;

        // TUYỆT ĐỐI KHÔNG GIẢI PHÓNG ActiveSlotOccupancy: giữ nguyên để chờ worker thu hồi kết quả muộn hoặc đối soát an toàn
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CancellationHandlingResult(
            JobId: jobId,
            FinalState: job.State,
            CancelRequested: true,
            OccupancyReleased: false,
            Message: "Đã ghi nhận yêu cầu hủy tác vụ. Do tác dụng phụ đã gửi ra ngoài, slot occupancy được giữ nguyên để chờ worker thu hồi kết quả muộn hoặc đối soát."
        );
    }

    /// <summary>
    /// Alias đồng bộ/bất đồng bộ ngắn hơn cho HandleCancellationRequestAsync.
    /// </summary>
    public static Task<CancellationHandlingResult> HandleCancellationRequest(
        TiToAIDbContext dbContext,
        string jobId,
        CancellationToken cancellationToken = default,
        Func<DateTimeOffset>? clock = null)
        => HandleCancellationRequestAsync(dbContext, jobId, cancellationToken, clock);

    /// <summary>
    /// Xử lý kết quả đến muộn (Late Result) trên tác vụ đã có yêu cầu hủy (CancelRequested == 1 hoặc đã Canceled):
    /// - Thu hồi và lưu trữ kết quả (ResultReference / ResultText) an toàn.
    /// - Trạng thái Job PHẢI LÀ Canceled (với reason user_canceled hoặc canceled_late_result).
    /// - TUYỆT ĐỐI KHÔNG ĐƯỢC CHUYỂN THÀNH Succeeded!
    /// - Sau khi lưu kết quả và chuyển Canceled, giải phóng slot occupancy an toàn.
    /// - Nếu Zombie Worker (fencing token không khớp hoặc lease đã quá hạn): Ném FencingTokenStaleException và chặn tuyệt đối không cho ghi đè DB.
    /// </summary>
    public static async Task<LateResultHandlingResult> HandleLateResultOnCanceledJobAsync(
        TiToAIDbContext dbContext,
        string jobId,
        long fencingToken,
        string? resultReference,
        string? resultText,
        CancellationToken cancellationToken = default,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        var nowIso = now.ToString("o");

        var job = await dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            throw new JobNotFoundException(jobId);
        }

        // 1. ZOMBIE WORKER GUARD: Fencing token không khớp
        if (job.FencingToken != fencingToken)
        {
            throw new FencingTokenStaleException(jobId, fencingToken);
        }

        // 2. ZOMBIE WORKER GUARD: Lease đã hết hạn trong DB
        if (job.LeaseExpiresAtUtc is not null && string.Compare(job.LeaseExpiresAtUtc, nowIso, StringComparison.Ordinal) < 0)
        {
            throw new FencingTokenStaleException(jobId, fencingToken);
        }

        // 3. ZOMBIE WORKER GUARD: Job đã bị Crash Recovery hoặc Reconcile chuyển sang Failed hoặc AwaitingUser
        if (job.State == JobState.Failed || job.State == JobState.AwaitingUser)
        {
            throw new FencingTokenStaleException(jobId, fencingToken);
        }

        // 4. Tuyệt đối không can thiệp nếu job đã kết thúc thành công bình thường trước đó
        if (job.State == JobState.Succeeded)
        {
            throw new JobConcurrencyConflictException($"Tác vụ '{jobId}' đã kết thúc thành công trước đó, không thể ghi đè kết quả hủy.", jobId);
        }

        // 5. Thu hồi và lưu trữ kết quả an toàn
        job.ResultReference = resultReference;
        job.ResultText = resultText;

        // 6. Trạng thái Job PHẢI LÀ Canceled (TUYỆT ĐỐI KHÔNG ĐƯỢC CHUYỂN THÀNH Succeeded!)
        job.State = JobState.Canceled;
        job.ReasonCode = JobReasonCode.CanceledLateResult;
        job.LeaseExpiresAtUtc = null;
        job.LeaseOwnerId = null;
        job.UpdatedAtUtc = nowIso;

        // Nếu kết quả sinh ra Asset, đánh dấu Asset là "Unlinked" để bảo vệ an toàn file vật lý
        if (!string.IsNullOrWhiteSpace(resultReference))
        {
            var asset = await dbContext.Assets
                .FirstOrDefaultAsync(a => a.Id == resultReference, cancellationToken)
                .ConfigureAwait(false);
            if (asset is not null)
            {
                asset.Status = "Unlinked";
            }
        }

        // 7. Giải phóng slot occupancy an toàn
        bool occupancyReleased = false;
        var occupancy = await dbContext.ActiveSlotOccupancies
            .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken)
            .ConfigureAwait(false);

        if (occupancy is not null)
        {
            dbContext.ActiveSlotOccupancies.Remove(occupancy);
            occupancyReleased = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new LateResultHandlingResult(
            JobId: jobId,
            FinalState: JobState.Canceled,
            ResultReference: resultReference,
            ResultText: resultText,
            OccupancyReleased: occupancyReleased
        );
    }

    /// <summary>
    /// Alias đồng bộ/bất đồng bộ ngắn hơn cho HandleLateResultOnCanceledJobAsync.
    /// </summary>
    public static Task<LateResultHandlingResult> HandleLateResultOnCanceledJob(
        TiToAIDbContext dbContext,
        string jobId,
        long fencingToken,
        string? resultReference,
        string? resultText,
        CancellationToken cancellationToken = default,
        Func<DateTimeOffset>? clock = null)
        => HandleLateResultOnCanceledJobAsync(dbContext, jobId, fencingToken, resultReference, resultText, cancellationToken, clock);
}
