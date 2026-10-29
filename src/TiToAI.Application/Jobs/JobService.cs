using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TiToAI.Contracts.Jobs;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Application.Jobs;

/// <summary>
/// Dịch vụ nghiệp vụ quản lý vòng đời Hàng đợi Tác vụ Bền vững (Job Queue)
/// theo kiến trúc T15 và hợp đồng JobDtos.
/// </summary>
public sealed class JobService
{
    private readonly DbContextOptions<TiToAIDbContext> _dbOptions;
    private readonly JobSnapshotFactory _snapshotFactory;
    private readonly JobScheduler _scheduler;
    private readonly IJobWakeupNotifier _wakeupNotifier;
    private readonly JobReconciliationService _reconciliationService;
    private readonly Func<DateTimeOffset>? _clock;

    public JobService(
        DbContextOptions<TiToAIDbContext> dbOptions,
        JobSnapshotFactory snapshotFactory,
        JobScheduler scheduler,
        IJobWakeupNotifier? wakeupNotifier = null,
        Func<DateTimeOffset>? clock = null)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _wakeupNotifier = wakeupNotifier ?? scheduler;
        _reconciliationService = new JobReconciliationService(_dbOptions, _wakeupNotifier, clock);
        _clock = clock;
    }

    private DateTimeOffset UtcNow => _clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public void NotifyWorkflowRunReady()
    {
        _wakeupNotifier.NotifyWakeup(new JobWakeupSignal("workflow.run_ready"));
    }

    /// <summary>
    /// Xếp hàng tác vụ mới (job.enqueue): Xác thực ranh giới, đóng băng snapshot bất biến và lưu vào SQLite.
    /// Kích hoạt tín hiệu đánh thức qua Channel để Scheduler phân phối ngay lập tức.
    /// </summary>
    public Task<JobEnqueueResponseData> EnqueueAsync(
        JobEnqueueRequest request,
        CancellationToken cancellationToken = default)
        => EnqueueCoreAsync(request, null, cancellationToken);

    internal Task<JobEnqueueResponseData> StageWorkflowJobAsync(
        JobEnqueueRequest request, TiToAIDbContext context, CancellationToken cancellationToken)
        => EnqueueCoreAsync(request, context, cancellationToken);

    private async Task<JobEnqueueResponseData> EnqueueCoreAsync(
        JobEnqueueRequest request, TiToAIDbContext? workflowContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            throw new JobValidationException("Trường 'projectId' không được để trống.", nameof(request.ProjectId));
        }

        if (!JobTaskType.IsValid(request.TaskType))
        {
            throw new JobValidationException($"Loại tác vụ '{request.TaskType}' không hợp lệ. Các loại cho phép: {string.Join(", ", JobTaskType.All)}.", nameof(request.TaskType));
        }

        if (string.IsNullOrWhiteSpace(request.WorkflowRunId) != string.IsNullOrWhiteSpace(request.WorkflowStepKey))
        {
            throw new JobValidationException(
                "WorkflowRunId và WorkflowStepKey phải được truyền cùng nhau.",
                nameof(request.WorkflowStepKey));
        }

        var workflowIdempotencyKey = CreateWorkflowIdempotencyKey(request);

        await using var ownedContext = workflowContext == null ? new TiToAIDbContext(_dbOptions) : null;
        var dbContext = workflowContext ?? ownedContext!;

        // Xác thực Project aggregate.
        var projectExists = await dbContext.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == request.ProjectId, cancellationToken).ConfigureAwait(false);

        if (!projectExists)
        {
            throw new JobValidationException($"Không tìm thấy Dự án '{request.ProjectId}'.", nameof(request.ProjectId));
        }

        // 3. Xác thực Phân cảnh (CK_Job_TaskType_Scene: Script => null, Image/Video* => bắt buộc)
        string? normalizedSceneId = null;
        if (request.TaskType == JobTaskType.Script)
        {
            normalizedSceneId = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.SceneId))
            {
                throw new JobValidationException($"Tác vụ '{request.TaskType}' bắt buộc phải thuộc một Phân cảnh cụ thể.", nameof(request.SceneId));
            }

            normalizedSceneId = request.SceneId.Trim();
            var sceneExists = await dbContext.Scenes
                .AsNoTracking()
                .AnyAsync(s => s.Id == normalizedSceneId && s.ProjectId == request.ProjectId, cancellationToken).ConfigureAwait(false);

            if (!sceneExists)
            {
                throw new JobValidationException($"Không tìm thấy Phân cảnh '{normalizedSceneId}' thuộc Dự án '{request.ProjectId}'.", nameof(request.SceneId));
            }
        }

        if (workflowIdempotencyKey != null)
        {
            var existingJobId = await dbContext.AppSettings
                .AsNoTracking()
                .Where(setting => setting.Key == workflowIdempotencyKey)
                .Select(setting => setting.Value)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (existingJobId != null)
            {
                var existingJob = await dbContext.Jobs.AsNoTracking()
                    .FirstOrDefaultAsync(job => job.Id == existingJobId, cancellationToken).ConfigureAwait(false);
                if (existingJob == null)
                {
                    throw new InvalidOperationException("Workflow idempotency record points to a missing job.");
                }

                ValidateWorkflowJobIdentity(existingJob, request, normalizedSceneId);
                return ToEnqueueResponse(existingJob);
            }
        }

        // 4. Chụp bản sao bất biến của các tham số đầu vào (Provider, Storage Root, Prompt, Config)
        var snapshot = await _snapshotFactory.CreateSnapshotAsync(request, cancellationToken).ConfigureAwait(false);

        var nowIso = UtcNow.ToString("o");
        var jobId = Guid.NewGuid().ToString();

        // 5. Khởi tạo thực thể Job bền vững trong SQLite
        var jobEntity = new Job
        {
            Id = jobId,
            ProjectId = request.ProjectId,
            SceneId = normalizedSceneId,
            TaskType = request.TaskType,
            State = JobState.Queued,
            ProviderKey = snapshot.ProviderKey,
            StorageRootId = snapshot.StorageRootId,
            PromptSnapshotText = snapshot.PromptSnapshotText,
            ConfigSnapshotJson = snapshot.ConfigSnapshotJson,
            Attempts = 0,
            MaxAttempts = 3,
            FencingToken = 0,
            SideEffectSubmitted = 0,
            CancelRequested = 0,
            CreatedAtUtc = nowIso,
            UpdatedAtUtc = nowIso
        };

        dbContext.Jobs.Add(jobEntity);
        if (workflowIdempotencyKey != null)
        {
            dbContext.AppSettings.Add(new AppSetting
            {
                Key = workflowIdempotencyKey,
                Value = jobId,
                UpdatedAtUtc = nowIso
            });
        }

        if (workflowContext != null)
        {
            return ToEnqueueResponse(jobEntity);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (workflowIdempotencyKey != null)
        {
            // AppSetting.Key is the unique claim. If another dispatcher won, its job is the replay result.
            await using var winnerContext = new TiToAIDbContext(_dbOptions);
            var winnerId = await winnerContext.AppSettings.AsNoTracking()
                .Where(setting => setting.Key == workflowIdempotencyKey)
                .Select(setting => setting.Value)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (winnerId == null)
            {
                throw;
            }

            var winner = await winnerContext.Jobs.AsNoTracking()
                .FirstOrDefaultAsync(job => job.Id == winnerId, cancellationToken).ConfigureAwait(false);
            if (winner == null)
            {
                throw;
            }

            ValidateWorkflowJobIdentity(winner, request, normalizedSceneId);
            return ToEnqueueResponse(winner);
        }

        // 6. Gửi tín hiệu đánh thức tức thời tới Scheduler qua Channel
        _wakeupNotifier.NotifyWakeup(new JobWakeupSignal(
            Source: "job.enqueue",
            JobId: jobId
        ));

        return ToEnqueueResponse(jobEntity);
    }

    private static string? CreateWorkflowIdempotencyKey(JobEnqueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkflowRunId) || string.IsNullOrWhiteSpace(request.WorkflowStepKey))
        {
            return null;
        }

        var material = string.Join("\0", request.ProjectId, request.WorkflowRunId, request.WorkflowStepKey);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return $"workflow.job.{hash}";
    }

    private static void ValidateWorkflowJobIdentity(Job job, JobEnqueueRequest request, string? sceneId)
    {
        if (job.ProjectId != request.ProjectId ||
            job.SceneId != sceneId || job.TaskType != request.TaskType)
        {
            throw new JobValidationException(
                "Workflow step key đã được dùng cho một job thuộc Project hoặc loại tác vụ khác.",
                nameof(request.WorkflowStepKey));
        }
    }

    private static JobEnqueueResponseData ToEnqueueResponse(Job job) => new(
        JobId: job.Id,
        State: job.State,
        TaskType: job.TaskType,
        ProviderKey: job.ProviderKey,
        StorageRootId: job.StorageRootId,
        CreatedAtUtc: job.CreatedAtUtc,
        Revision: job.Revision);

    /// <summary>
    /// Lấy chi tiết trạng thái của một tác vụ (job.get_status).
    /// </summary>
    public async Task<JobGetStatusResponseData> GetStatusAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using var dbContext = new TiToAIDbContext(_dbOptions);

        var job = await dbContext.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);

        if (job is null)
        {
            throw new JobNotFoundException(jobId);
        }

        var binding = await dbContext.ExecutionBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.JobId == jobId, cancellationToken).ConfigureAwait(false);

        bool hasOccupancy = await dbContext.ActiveSlotOccupancies
            .AsNoTracking()
            .AnyAsync(o => o.JobId == jobId, cancellationToken).ConfigureAwait(false);

        bool hasUnresolved = hasOccupancy ||
            ((job.State == JobState.Canceled || job.State == JobState.Failed || job.State == JobState.AwaitingUser) &&
             job.SideEffectSubmitted == 1 &&
             string.IsNullOrWhiteSpace(job.ResultReference) &&
             string.IsNullOrWhiteSpace(job.ResultText));

        var detail = new JobDetailDto(
            Id: job.Id,
            ProjectId: job.ProjectId,
            SceneId: job.SceneId,
            TaskType: job.TaskType,
            State: job.State,
            ReasonCode: job.ReasonCode,
            ProviderKey: job.ProviderKey,
            StorageRootId: job.StorageRootId,
            Attempts: job.Attempts,
            MaxAttempts: job.MaxAttempts,
            SideEffectSubmitted: job.SideEffectSubmitted,
            ResultReference: job.ResultReference,
            ResultText: job.ResultText,
            CancelRequested: job.CancelRequested == 1,
            CreatedAtUtc: job.CreatedAtUtc,
            UpdatedAtUtc: job.UpdatedAtUtc,
            ProfileId: binding?.ProfileId,
            HasUnresolvedSideEffect: hasUnresolved,
            Revision: job.Revision,
            Activity: (await new JobActivityLog(_dbOptions).ReadAsync(jobId, cancellationToken).ConfigureAwait(false))
                .Select(entry => new JobActivityEntryDto(entry.AtUtc, entry.Step, entry.Message)).ToArray()
        );

        return new JobGetStatusResponseData(detail);
    }

    /// <summary>
    /// Liệt kê các tác vụ đang hoạt động (job.list_active): Queued, Reserved, Running, AwaitingUser
    /// và các tác vụ terminal (Canceled/Failed) còn unresolved (đang giữ ActiveSlotOccupancy hoặc side-effect in-flight chưa giải quyết).
    /// </summary>
    public async Task<JobListActiveResponseData> ListActiveAsync(
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new TiToAIDbContext(_dbOptions);

        var query = dbContext.Jobs
            .AsNoTracking()
            .Where(j => JobState.ActiveStates.Contains(j.State)
                || ((j.State == JobState.Canceled || j.State == JobState.Failed) &&
                    (dbContext.ActiveSlotOccupancies.Any(o => o.JobId == j.Id) ||
                     (j.SideEffectSubmitted == 1 && (j.ResultReference == null || j.ResultReference == "") && (j.ResultText == null || j.ResultText == "")))));

        var jobs = await query
            .OrderBy(j => j.CreatedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var jobIds = jobs.Select(j => j.Id).ToList();

        var bindings = await dbContext.ExecutionBindings
            .AsNoTracking()
            .Where(b => jobIds.Contains(b.JobId))
            .ToDictionaryAsync(b => b.JobId, b => b.ProfileId, cancellationToken).ConfigureAwait(false);

        var occupancies = await dbContext.ActiveSlotOccupancies
            .AsNoTracking()
            .Where(o => jobIds.Contains(o.JobId))
            .Select(o => o.JobId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var occupancySet = new HashSet<string>(occupancies);

        var activeJobs = jobs.Select(j =>
        {
            bindings.TryGetValue(j.Id, out var profileId);
            bool hasUnresolved = occupancySet.Contains(j.Id) ||
                ((j.State == JobState.Canceled || j.State == JobState.Failed || j.State == JobState.AwaitingUser) &&
                 j.SideEffectSubmitted == 1 &&
                 string.IsNullOrWhiteSpace(j.ResultReference) &&
                 string.IsNullOrWhiteSpace(j.ResultText));

            return new JobSummaryDto(
                Id: j.Id,
                ProjectId: j.ProjectId,
                SceneId: j.SceneId,
                TaskType: j.TaskType,
                State: j.State,
                ReasonCode: j.ReasonCode,
                Attempts: j.Attempts,
                ProgressPercent: null,
                CurrentStep: null,
                CreatedAtUtc: j.CreatedAtUtc,
                MaxAttempts: j.MaxAttempts,
                SideEffectSubmitted: j.SideEffectSubmitted,
                CancelRequested: j.CancelRequested == 1,
                HasUnresolvedSideEffect: hasUnresolved,
                ProfileId: profileId,
                ResultReference: j.ResultReference,
                Revision: j.Revision,
                ResultText: j.ResultText
            );
        }).ToList();

        return new JobListActiveResponseData(activeJobs);
    }

    /// <summary>
    /// Hủy bỏ tác vụ (job.cancel):
    /// - Nếu đang Queued: Chuyển ngay sang Canceled.
    /// - Nếu đang Reserved/Running và chưa submit side-effect: Chuyển ngay sang Canceled.
    /// - Nếu đã submit side-effect: Đánh dấu cancel_requested = 1 để đối soát/worker xử lý an toàn.
    /// - Nếu đang AwaitingUser: Chuyển ngay sang Canceled.
    /// </summary>
    public async Task<JobCancelResponseData> CancelAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using var dbContext = new TiToAIDbContext(_dbOptions);

        var job = await dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);

        if (job is null)
        {
            throw new JobNotFoundException(jobId);
        }

        if (JobState.IsTerminal(job.State))
        {
            throw new JobConcurrencyConflictException($"Tác vụ đã kết thúc ở trạng thái '{job.State}', không thể hủy bỏ.", jobId);
        }

        var prevState = job.State;
        var nowIso = UtcNow.ToString("o");

        if (job.State == JobState.Queued || (job.State is JobState.Reserved or JobState.Running && job.SideEffectSubmitted == 0))
        {
            job.State = JobState.Canceled;
            job.CancelRequested = 1;
            job.ReasonCode = JobReasonCode.UserCanceled;
            job.LeaseExpiresAtUtc = null;
            job.LeaseOwnerId = null;
            job.Revision += 1;
            job.UpdatedAtUtc = nowIso;

            // Giải phóng slot occupancy nếu đã được cấp
            var occupancy = await dbContext.ActiveSlotOccupancies
                .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken).ConfigureAwait(false);
            if (occupancy != null)
            {
                dbContext.ActiveSlotOccupancies.Remove(occupancy);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new JobCancelResponseData(
                JobId: jobId,
                State: JobState.Canceled,
                CancelRequested: false,
                Message: "Tác vụ đã được hủy bỏ thành công trước khi gửi side-effect.",
                PreviousState: prevState,
                Revision: job.Revision
            );
        }

        if (job.State == JobState.AwaitingUser)
        {
            job.State = JobState.Canceled;
            job.CancelRequested = 1;
            job.ReasonCode = JobReasonCode.UserCanceled;
            job.LeaseExpiresAtUtc = null;
            job.Revision += 1;
            job.UpdatedAtUtc = nowIso;

            // T16 FINDING 2: Nếu job đã submit side effect nhưng chưa có kết quả (unresolved),
            // CẤM giải phóng ActiveSlotOccupancy để bảo vệ capacity cho đến khi đối soát an toàn!
            bool hasUnresolvedSideEffect = job.SideEffectSubmitted == 1 &&
                                           string.IsNullOrWhiteSpace(job.ResultReference) &&
                                           string.IsNullOrWhiteSpace(job.ResultText);

            if (!hasUnresolvedSideEffect)
            {
                var occupancy = await dbContext.ActiveSlotOccupancies
                    .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken).ConfigureAwait(false);
                if (occupancy != null)
                {
                    dbContext.ActiveSlotOccupancies.Remove(occupancy);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new JobCancelResponseData(
                JobId: jobId,
                State: JobState.Canceled,
                CancelRequested: false,
                Message: hasUnresolvedSideEffect
                    ? "Tác vụ đã được hủy bỏ. Do có tác dụng phụ chưa giải quyết, slot occupancy được bảo lưu cho đến khi đối soát an toàn."
                    : "Tác vụ đang chờ đối soát đã được người dùng hủy bỏ.",
                PreviousState: prevState,
                Revision: job.Revision
            );
        }

        // Job đang Running và đã submit side effect
        job.CancelRequested = 1;
        job.Revision += 1;
        job.UpdatedAtUtc = nowIso;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new JobCancelResponseData(
            JobId: jobId,
            State: job.State,
            CancelRequested: true,
            Message: "Đã ghi nhận yêu cầu hủy tác vụ. Do tác dụng phụ đã gửi ra ngoài, hệ thống sẽ bảo vệ kết quả hoặc chuyển sang đối soát.",
            PreviousState: prevState,
            Revision: job.Revision
        );
    }

    /// <summary>
    /// Tiếp tục tác vụ đang AwaitingUser (job.resume).
    /// Kiểm tra tính đủ điều kiện thông qua Browser Gatekeeper / Session Checkpoint Coordinator.
    /// </summary>
    public async Task<JobResumeResponseData> ResumeAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var job = await dbContext.Jobs
            .Include(j => j.ExecutionBinding)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            throw new JobNotFoundException(jobId);
        }

        if (job.State != JobState.AwaitingUser)
        {
            throw new JobConcurrencyConflictException(
                $"Chỉ có thể tiếp tục (resume) tác vụ ở trạng thái 'AwaitingUser', trạng thái hiện tại: '{job.State}'.",
                jobId);
        }

        if (JobRetryAndCancelPolicy.IsAutomationUnavailable(job.ReasonCode))
        {
            throw new JobValidationException(
                "Tự động hóa chưa sẵn sàng cho tác vụ này. Hãy hủy tác vụ cũ và tạo yêu cầu mới sau khi adapter/capability đã sẵn sàng.",
                nameof(job.ReasonCode));
        }

        var profileId = job.ExecutionBinding?.ProfileId;
        bool sideEffectSubmitted = job.SideEffectSubmitted == 1;

        if (!string.IsNullOrWhiteSpace(profileId) && _scheduler.Gatekeeper != null)
        {
            bool eligible = _scheduler.Gatekeeper.CanResumeJob(profileId, jobId, sideEffectSubmitted);
            if (!eligible)
            {
                throw new JobValidationException(
                    "Tác vụ chưa đủ điều kiện tiếp tục: trình duyệt chưa ở trạng thái ẩn (Hidden), phiên chưa kết nối hoặc danh tính không khớp.",
                    jobId);
            }
        }

        bool hasCheckpoint = !string.IsNullOrWhiteSpace(job.ResultText) || !string.IsNullOrWhiteSpace(job.ResultReference);
        string targetState = (sideEffectSubmitted && !hasCheckpoint) ? JobState.AwaitingUser : JobState.Queued;
        job.State = targetState;
        job.ReasonCode = (sideEffectSubmitted && !hasCheckpoint) ? JobReasonCode.ReconcileRequired : null;
        if (targetState == JobState.Queued)
        {
            job.RetryAfterUtc = null;
            job.LeaseExpiresAtUtc = null;
            job.LeaseOwnerId = null;
        }
        job.Revision += 1;
        job.UpdatedAtUtc = UtcNow.ToString("o");
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _wakeupNotifier?.NotifyWakeup(new JobWakeupSignal("job.resume", jobId, profileId));

        return new JobResumeResponseData(
            JobId: jobId,
            State: targetState,
            Message: (sideEffectSubmitted && !hasCheckpoint)
                ? "Tác vụ có side-effect đã được chuyển sang chế độ đối soát."
                : "Tác vụ đã được tiếp tục và đưa trở lại hàng đợi thành công.",
            Revision: job.Revision
        );
    }

    /// <summary>
    /// Thử lại tác vụ bị thất bại (job.retry) theo contract v1.1 §5:
    /// - Chỉ áp dụng cho Job ở trạng thái Failed
    /// - SideEffectSubmitted == 0 và không có tác dụng phụ chưa giải quyết (không giữ occupancy)
    /// - CancelRequested == 0
    /// - Attempts &lt; MaxAttempts
    /// - ReasonCode không nằm trong danh sách không thể retry
    /// - So khớp ExpectedRevision (Optimistic Concurrency)
    /// - Đưa về Queued, tăng Revision, bảo toàn ExecutionBinding và attempts hiện có.
    /// </summary>
    public async Task<JobRetryResponseData> RetryAsync(
        JobRetryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.JobId);

        await using var dbContext = new TiToAIDbContext(_dbOptions);

        var job = await dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == request.JobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            throw new JobNotFoundException(request.JobId);
        }

        if (job.Revision != request.ExpectedRevision)
        {
            throw new JobConcurrencyConflictException(
                $"Xung đột phiên bản tác vụ: phiên bản kỳ vọng là {request.ExpectedRevision}, phiên bản hiện tại là {job.Revision}.",
                job.Id);
        }

        if (job.State != JobState.Failed)
        {
            throw new JobConcurrencyConflictException(
                $"Chỉ có thể thử lại (retry) tác vụ ở trạng thái 'Failed', trạng thái hiện tại là '{job.State}'.",
                job.Id);
        }

        // Render MuseAI hết thời gian chờ sau khi đã gửi: chỉ thử lại bằng cách tiếp tục chờ side chat đã lưu, không gửi lại.
        bool resumableRender = JobRetryAndCancelPolicy.IsResumableRenderReason(job.ReasonCode)
            && await TiToAI.Application.Providers.MuseAiPlaywrightAutomation.CanResumeRenderAsync(dbContext, job, cancellationToken).ConfigureAwait(false);

        if (job.SideEffectSubmitted != 0 && !resumableRender)
        {
            throw new JobValidationException(
                "Không thể thử lại (retry) tác vụ đã phát sinh tác dụng phụ (side-effect) ra bên ngoài.",
                nameof(job.SideEffectSubmitted));
        }

        bool hasOccupancy = await dbContext.ActiveSlotOccupancies
            .AsNoTracking()
            .AnyAsync(o => o.JobId == job.Id, cancellationToken).ConfigureAwait(false);

        bool hasUnresolved = hasOccupancy ||
            (job.SideEffectSubmitted == 1 && !resumableRender &&
             string.IsNullOrWhiteSpace(job.ResultReference) &&
             string.IsNullOrWhiteSpace(job.ResultText));

        if (hasUnresolved)
        {
            throw new JobValidationException(
                "Tác vụ có tác dụng phụ chưa được giải quyết, không thể retry.",
                nameof(job.SideEffectSubmitted));
        }

        if (job.CancelRequested != 0)
        {
            throw new JobValidationException(
                "Tác vụ đã yêu cầu hủy bỏ bởi người dùng, không thể retry.",
                nameof(job.CancelRequested));
        }

        var nonRetryableReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            JobReasonCode.UserCanceled,
            JobReasonCode.CanceledLateResult,
            JobReasonCode.LoginRequired,
            JobReasonCode.SessionExpired,
            JobReasonCode.AuthClosed,
            JobReasonCode.WrongAccount,
            JobReasonCode.StorageRootUnavailable,
            "adapter_not_available"
        };

        if (JobRetryAndCancelPolicy.IsAutomationUnavailable(job.ReasonCode)
            || (!string.IsNullOrWhiteSpace(job.ReasonCode) && nonRetryableReasons.Contains(job.ReasonCode)))
        {
            throw new JobValidationException(
                $"Tác vụ thất bại với lý do '{job.ReasonCode}' không đủ điều kiện thử lại tự động.",
                nameof(job.ReasonCode));
        }

        if (job.Attempts >= job.MaxAttempts)
        {
            throw new JobValidationException(
                $"Tác vụ đã vượt quá ngân sách số lần thử tối đa ({job.Attempts}/{job.MaxAttempts}). Hết ngân sách muốn chạy lại phải tạo tác vụ mới có chủ ý.",
                nameof(job.Attempts));
        }

        job.State = JobState.Queued;
        job.ReasonCode = null;
        job.RetryAfterUtc = null;
        job.LeaseExpiresAtUtc = null;
        job.LeaseOwnerId = null;
        job.Revision += 1;
        job.UpdatedAtUtc = UtcNow.ToString("o");

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _wakeupNotifier?.NotifyWakeup(new JobWakeupSignal("job.retry", request.JobId));

        return new JobRetryResponseData(
            JobId: job.Id,
            State: job.State,
            Revision: job.Revision,
            Message: "Tác vụ đã được đưa trở lại hàng đợi để thử lại thành công."
        );
    }

    /// <summary>
    /// Liệt kê lịch sử các tác vụ đã hoàn tất và được giải quyết an toàn (job.list_history) theo contract v1.1 §5:
    /// - Chỉ trả về các job ở trạng thái terminal: Succeeded, Failed, Canceled
    /// - Chỉ trả về các job đã resolved: KHÔNG giữ ActiveSlotOccupancy và KHÔNG có unresolved side-effect
    /// - Sắp xếp theo updatedAtUtc DESC rồi id DESC
    /// - Phân trang theo cursor opaque (base64 của updatedAtUtc|id)
    /// - PageSize: 1–100 (mặc định 20)
    /// </summary>
    public async Task<JobListHistoryResponseData> ListHistoryAsync(
        JobListHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        int pageSize = request.PageSize switch
        {
            null or <= 0 => 20,
            > 100 => 100,
            _ => request.PageSize.Value
        };

        await using var dbContext = new TiToAIDbContext(_dbOptions);

        var query = dbContext.Jobs
            .AsNoTracking()
            .Where(j => JobState.TerminalStates.Contains(j.State)
                && !dbContext.ActiveSlotOccupancies.Any(o => o.JobId == j.Id)
                && !((j.State == JobState.Canceled || j.State == JobState.Failed) &&
                     j.SideEffectSubmitted == 1 &&
                     (j.ResultReference == null || j.ResultReference == "") &&
                     (j.ResultText == null || j.ResultText == "")));

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            query = query.Where(j => j.ProjectId == request.ProjectId);
        }

        if (!string.IsNullOrWhiteSpace(request.Cursor))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(request.Cursor));
                var parts = decoded.Split('|', 2);
                if (parts.Length == 2)
                {
                    var cursorUpdatedAt = parts[0];
                    var cursorId = parts[1];
                    query = query.Where(j => string.Compare(j.UpdatedAtUtc, cursorUpdatedAt) < 0 ||
                                            (j.UpdatedAtUtc == cursorUpdatedAt && string.Compare(j.Id, cursorId) < 0));
                }
            }
            catch
            {
                throw new JobValidationException("Cursor phân trang không hợp lệ.", nameof(request.Cursor));
            }
        }

        query = query
            .OrderByDescending(j => j.UpdatedAtUtc)
            .ThenByDescending(j => j.Id);

        var itemsWithExtra = await query
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        bool hasMore = itemsWithExtra.Count > pageSize;
        var pagedJobs = hasMore ? itemsWithExtra.Take(pageSize).ToList() : itemsWithExtra;

        var jobIds = pagedJobs.Select(j => j.Id).ToList();
        var bindings = await dbContext.ExecutionBindings
            .AsNoTracking()
            .Where(b => jobIds.Contains(b.JobId))
            .ToDictionaryAsync(b => b.JobId, b => b.ProfileId, cancellationToken).ConfigureAwait(false);

        string? nextCursor = null;
        if (hasMore && pagedJobs.Count > 0)
        {
            var last = pagedJobs[^1];
            nextCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{last.UpdatedAtUtc}|{last.Id}"));
        }

        var dtos = pagedJobs.Select(j =>
        {
            bindings.TryGetValue(j.Id, out var profileId);
            return new JobSummaryDto(
                Id: j.Id,
                ProjectId: j.ProjectId,
                SceneId: j.SceneId,
                TaskType: j.TaskType,
                State: j.State,
                ReasonCode: j.ReasonCode,
                Attempts: j.Attempts,
                ProgressPercent: null,
                CurrentStep: null,
                CreatedAtUtc: j.CreatedAtUtc,
                MaxAttempts: j.MaxAttempts,
                SideEffectSubmitted: j.SideEffectSubmitted,
                CancelRequested: j.CancelRequested == 1,
                HasUnresolvedSideEffect: false,
                ProfileId: profileId,
                ResultReference: j.ResultReference,
                Revision: j.Revision,
                ResultText: j.ResultText
            );
        }).ToList();

        return new JobListHistoryResponseData(dtos, nextCursor);
    }

    /// <summary>
    /// Đối soát tác vụ bị treo hoặc yêu cầu người dùng can thiệp (job.reconcile):
    /// Áp dụng cho các tác vụ ở trạng thái AwaitingUser.
    /// Ủy quyền cho JobReconciliationService để đảm bảo tuân thủ đầy đủ execution-contract-v1.1 và giải phóng occupancy.
    /// </summary>
    public Task<JobReconcileResponseData> ReconcileAsync(
        JobReconcileRequest request,
        CancellationToken cancellationToken = default)
    {
        return _reconciliationService.ReconcileAsync(request, cancellationToken);
    }

    /// <summary>
    /// Chạy quét phục hồi sự cố khởi động (3-tier startup recovery sweep).
    /// </summary>
    public Task<int> RunCrashRecoverySweepAsync(CancellationToken cancellationToken = default)
    {
        return _scheduler.RunCrashRecoverySweepAsync(cancellationToken);
    }
}
