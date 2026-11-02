using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Content;

/// <summary>
/// Mô hình bước logic của run (docs/plans/20261102-retry-failed-video-step.md §3): lần thử hiện hành của từng
/// (loại bước, cảnh) và kết quả của nó. Dùng cho hiển thị và cho retry thủ công; pipeline không dùng để điều khiển.
/// </summary>
public sealed partial class ProjectWorkflowService
{
    private const string RejectedMarkerPrefix = "workflow.rejected.";
    private const string AppliedMarkerPrefix = "workflow.applied.";
    private const string RetryStepKeySeparator = "#retry.";

    /// <summary>Số job tối đa cho một bước logic trong một run khi retry kết quả bị loại (bằng MaxAttempts của job).</summary>
    private const int MaxJobsPerLogicalStep = 3;

    private sealed record RunStepsSnapshot(
        IReadOnlyList<WorkflowStepStateDto> Steps,
        bool HasPendingContinuation);

    private static string RejectedMarkerKey(string jobId) => RejectedMarkerPrefix + jobId;

    /// <summary>Ghi lý do kết quả bị loại cùng commit với marker applied; trạng thái bước đọc lại từ đây sau khi mở lại app.</summary>
    private static void AddRejectedMarker(FrayloDbContext dbContext, string jobId, string reason, string nowIso)
    {
        dbContext.AppSettings.Add(new AppSetting
        {
            Key = RejectedMarkerKey(jobId),
            Value = reason,
            UpdatedAtUtc = nowIso
        });
    }

    private static string? GetStepKind(Job job, bool gridMode)
    {
        switch (job.TaskType)
        {
            case JobTaskType.Script:
                return WorkflowStepKinds.Script;
            case JobTaskType.Image:
                if (!gridMode)
                {
                    return WorkflowStepKinds.Image;
                }
                var stepKey = GetSnapshotStepKey(job.ConfigSnapshotJson) ?? GetWorkflowStepKey(job.TaskType, job.SceneId);
                return stepKey.StartsWith(GridStepPrefix, StringComparison.Ordinal)
                    ? WorkflowStepKinds.Grid
                    : WorkflowStepKinds.Compose;
            case JobTaskType.VideoPrompt:
                return WorkflowStepKinds.Prompt;
            case JobTaskType.VideoRender:
                return WorkflowStepKinds.Render;
            default:
                return null;
        }
    }

    /// <summary>
    /// Lần thử hiện hành của mỗi bước logic trong <c>config.CurrentRunId</c>. Job cũ hơn của cùng bước chỉ là lịch sử.
    /// Chỉ đọc; không sửa DB.
    /// </summary>
    private async Task<RunStepsSnapshot> BuildRunStepsAsync(
        FrayloDbContext dbContext,
        ProjectWorkflowConfigDto config,
        bool withRetryInfo,
        CancellationToken cancellationToken)
    {
        var runId = config.CurrentRunId!;
        var gridMode = VideoWorkflowPromptComposer.IsGridMode(config);

        var runJobs = (await dbContext.Jobs.AsNoTracking()
                .Where(j => j.ProjectId == config.ProjectId && j.ConfigSnapshotJson != null && j.ConfigSnapshotJson.Contains(runId))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(j => GetWorkflowRunId(j.ConfigSnapshotJson) == runId)
            .OrderByDescending(j => j.CreatedAtUtc)
            .ThenByDescending(j => j.Id, StringComparer.Ordinal)
            .ToList();

        var groups = runJobs
            .Select(job => (Job: job, Kind: GetStepKind(job, gridMode)))
            .Where(x => x.Kind != null)
            .GroupBy(x => (Kind: x.Kind!, SceneId: x.Job.TaskType == JobTaskType.Script ? string.Empty : x.Job.SceneId ?? string.Empty))
            .ToList();

        var currentJobIds = groups.Select(g => g.First().Job.Id).ToList();
        var markerKeys = currentJobIds
            .SelectMany(id => new[] { AppliedMarkerPrefix + id, RejectedMarkerKey(id) })
            .ToList();
        var markers = markerKeys.Count == 0
            ? new Dictionary<string, string>()
            : await dbContext.AppSettings.AsNoTracking()
                .Where(s => markerKeys.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken).ConfigureAwait(false);

        var steps = new List<WorkflowStepStateDto>();
        foreach (var group in groups)
        {
            var job = group.First().Job;
            var attemptCount = group.Count();
            string outcome;
            string? reasonMessage = null;
            if (!JobState.IsTerminal(job.State))
            {
                outcome = WorkflowStepOutcomes.Pending;
            }
            else if (job.State == JobState.Failed)
            {
                outcome = WorkflowStepOutcomes.Failed;
            }
            else if (job.State == JobState.Canceled)
            {
                outcome = WorkflowStepOutcomes.Canceled;
            }
            else if (markers.TryGetValue(RejectedMarkerKey(job.Id), out var rejectedReason))
            {
                outcome = WorkflowStepOutcomes.Rejected;
                reasonMessage = rejectedReason;
            }
            else if (!markers.ContainsKey(AppliedMarkerPrefix + job.Id))
            {
                // Succeeded nhưng chưa apply: bước sau chưa sinh ra, chưa được coi là xong hay lỗi.
                outcome = WorkflowStepOutcomes.Pending;
            }
            else
            {
                outcome = WorkflowStepOutcomes.Succeeded;
            }

            var canRetry = false;
            string? blockedReason = null;
            if (withRetryInfo && outcome == WorkflowStepOutcomes.Failed)
            {
                blockedReason = _jobService == null
                    ? "Dịch vụ tác vụ chưa sẵn sàng."
                    : await _jobService.GetRetryBlockReasonAsync(job.Id, cancellationToken).ConfigureAwait(false);
                canRetry = blockedReason == null;
            }
            else if (withRetryInfo && outcome == WorkflowStepOutcomes.Rejected)
            {
                blockedReason = _jobService == null ? "Dịch vụ tác vụ chưa sẵn sàng." : GetRejectedRetryBlockReason(attemptCount);
                canRetry = blockedReason == null;
            }

            steps.Add(new WorkflowStepStateDto(
                Kind: group.Key.Kind,
                SceneId: group.Key.SceneId.Length == 0 ? null : group.Key.SceneId,
                JobId: job.Id,
                JobRevision: job.Revision,
                Outcome: outcome,
                ReasonCode: outcome == WorkflowStepOutcomes.Failed || outcome == WorkflowStepOutcomes.Canceled ? job.ReasonCode : null,
                ReasonMessage: reasonMessage,
                CanRetry: canRetry,
                BlockedReason: blockedReason,
                AttemptCount: attemptCount));
        }

        return new RunStepsSnapshot(steps, await HasPendingContinuationAsync(dbContext, config.ProjectId, runId, cancellationToken).ConfigureAwait(false));
    }

    private static string? GetRejectedRetryBlockReason(int jobsInStep) =>
        jobsInStep >= MaxJobsPerLogicalStep
            ? $"Đã thử {jobsInStep} lần, kết quả vẫn không hợp lệ. Hãy sửa mẫu/cấu hình rồi bắt đầu lại."
            : null;

    private static async Task<bool> HasPendingContinuationAsync(
        FrayloDbContext dbContext, string projectId, string runId, CancellationToken cancellationToken)
    {
        var values = await dbContext.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("workflow.continuation."))
            .Select(s => s.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var value in values)
        {
            try
            {
                var intent = JsonSerializer.Deserialize<WorkflowContinuationIntent>(value, JsonOptions);
                if (intent != null &&
                    string.Equals(intent.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(intent.RunId, runId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Intent hỏng đã được luồng chuyển tiếp xử lý riêng; không ảnh hưởng hiển thị.
            }
        }
        return false;
    }

    /// <summary>
    /// Trạng thái hiển thị của run: suy từ queue, không ghi DB. Pipeline KHÔNG dùng giá trị này để điều khiển
    /// (xem <see cref="GetStoredWorkflowConfigAsync"/>), vì một bước lỗi không được làm các cảnh khác bỏ việc.
    /// </summary>
    private static string DeriveDisplayRunState(string storedState, RunStepsSnapshot run)
    {
        if (run.Steps.Count == 0)
        {
            return storedState;
        }

        if (run.Steps.Any(s => s.Outcome == WorkflowStepOutcomes.Pending) || run.HasPendingContinuation)
        {
            // Retry đã được nhận nhưng config lưu còn Failed (vd: crash giữa enqueue và lưu): hiển thị đang chạy.
            return storedState == WorkflowRunState.Failed ? WorkflowRunState.Running : storedState;
        }

        return run.Steps.Any(s => s.Outcome is WorkflowStepOutcomes.Failed or WorkflowStepOutcomes.Rejected or WorkflowStepOutcomes.Canceled)
            ? WorkflowRunState.Failed
            : storedState;
    }
}
