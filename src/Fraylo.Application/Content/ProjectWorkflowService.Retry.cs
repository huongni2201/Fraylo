using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Application.Content;

/// <summary>
/// Retry thủ công đúng một bước lỗi (docs/plans/20261102-retry-failed-video-step.md §4.2).
/// Không retry tự động (NR1): chỉ chạy khi người dùng gửi lệnh.
/// </summary>
public sealed partial class ProjectWorkflowService
{
    /// <summary>
    /// Đưa lại đúng bước lỗi của run hiện tại vào hàng đợi. Job nguồn phải là lần thử hiện hành của bước:
    /// <list type="bullet">
    /// <item>Failed: <c>JobService.RetryAsync</c> (cùng job, cùng snapshot, giữ ngân sách attempts).</item>
    /// <item>Rejected (Succeeded nhưng kết quả bị loại): job mới cùng prompt và media đã lưu, stepKey riêng
    /// <c>{stepKey nguồn}#retry.{jobId nguồn}</c> để idempotency không trả về chính job bị loại.</item>
    /// </list>
    /// </summary>
    public async Task<RetryWorkflowStepResponseData> RetryWorkflowStepAsync(
        RetryWorkflowStepRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw new ProjectWorkflowValidationException("Thiếu tác vụ của bước cần thử lại.");
        }
        if (_jobService == null)
        {
            throw new ProjectWorkflowValidationException("Dịch vụ tác vụ chưa sẵn sàng nên chưa thể thử lại bước.");
        }

        return await RunUnderProjectGateAsync(projectId,
            () => RetryStepLockedAsync(projectId, request, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetryWorkflowStepResponseData> RetryStepLockedAsync(
        string projectId,
        RetryWorkflowStepRequest request,
        CancellationToken cancellationToken)
    {
        var jobService = _jobService!;
        var config = await GetStoredWorkflowConfigAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(config.CurrentRunId) ||
            config.RunState is WorkflowRunState.Idle or WorkflowRunState.Preparing or WorkflowRunState.Completed)
        {
            throw new ProjectWorkflowValidationException("Không có lượt sản xuất đang dừng để thử lại bước.");
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var run = await BuildRunStepsAsync(dbContext, config, withRetryInfo: true, cancellationToken).ConfigureAwait(false);
        var step = run.Steps.FirstOrDefault(s => s.JobId == request.JobId);
        if (step == null)
        {
            // Job thuộc run hiện tại nhưng đã có lần thử mới hơn (vd: bấm đúp): xung đột phiên bản, UI chỉ cần tải lại.
            var runId = config.CurrentRunId!;
            var supersededRunJob = await dbContext.Jobs.AsNoTracking().AnyAsync(
                j => j.Id == request.JobId && j.ProjectId == projectId && j.ConfigSnapshotJson != null && j.ConfigSnapshotJson.Contains(runId),
                cancellationToken).ConfigureAwait(false);
            if (supersededRunJob)
            {
                throw new JobConcurrencyConflictException("Bước này đã có lần thử mới hơn.", request.JobId);
            }
            // Sai Project hoặc run cũ.
            throw new ProjectWorkflowValidationException("Tác vụ này không thuộc lượt sản xuất hiện tại của Project.");
        }
        if (step.JobRevision != request.ExpectedJobRevision)
        {
            throw new JobConcurrencyConflictException(
                $"Xung đột phiên bản tác vụ: phiên bản kỳ vọng là {request.ExpectedJobRevision}, phiên bản hiện tại là {step.JobRevision}.",
                step.JobId);
        }
        if (step.Outcome is not (WorkflowStepOutcomes.Failed or WorkflowStepOutcomes.Rejected))
        {
            throw new ProjectWorkflowValidationException("Bước này không ở trạng thái lỗi nên không cần thử lại.");
        }
        if (!step.CanRetry)
        {
            throw new ProjectWorkflowValidationException(step.BlockedReason ?? "Bước này không thể thử lại.");
        }

        var sourceJob = await dbContext.Jobs.AsNoTracking().FirstAsync(j => j.Id == step.JobId, cancellationToken).ConfigureAwait(false);
        var media = ReadMediaSnapshot(sourceJob.ConfigSnapshotJson);
        await EnsureRetryInputsAvailableAsync(dbContext, media, cancellationToken).ConfigureAwait(false);

        string newJobId;
        long newRevision;
        if (step.Outcome == WorkflowStepOutcomes.Failed)
        {
            var retried = await jobService.RetryAsync(
                new JobRetryRequest(sourceJob.Id, request.ExpectedJobRevision), cancellationToken).ConfigureAwait(false);
            newJobId = retried.JobId;
            newRevision = retried.Revision;
        }
        else
        {
            var prompt = ReadRenderedPrompt(sourceJob.PromptSnapshotText)
                ?? throw new ProjectWorkflowValidationException("Không đọc được prompt đã lưu của bước này nên chưa thể thử lại.");
            var sourceStepKey = GetSnapshotStepKey(sourceJob.ConfigSnapshotJson) ?? GetWorkflowStepKey(sourceJob.TaskType, sourceJob.SceneId);
            var enqueued = await jobService.EnqueueAsync(new JobEnqueueRequest(
                ProjectId: projectId,
                SceneId: sourceJob.SceneId,
                TaskType: sourceJob.TaskType,
                CustomPromptText: prompt,
                MediaSnapshot: media,
                WorkflowRunId: config.CurrentRunId,
                WorkflowStepKey: $"{sourceStepKey}{RetryStepKeySeparator}{sourceJob.Id}"), cancellationToken).ConfigureAwait(false);
            newJobId = enqueued.JobId;
            newRevision = await dbContext.Jobs.AsNoTracking()
                .Where(j => j.Id == newJobId).Select(j => j.Revision).FirstAsync(cancellationToken).ConfigureAwait(false);
            jobService.NotifyWorkflowRunReady();
        }

        // Script parse lỗi đã lưu Failed; lần thử đã được nhận thì run chạy lại. Paused/Running giữ nguyên.
        var runState = config.RunState;
        if (runState == WorkflowRunState.Failed)
        {
            var key = GetConfigKey(projectId);
            var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
            if (setting != null)
            {
                var nowIso = DateTimeOffset.UtcNow.ToString("o");
                runState = WorkflowRunState.Running;
                setting.Value = SerializeStoredConfig(config with { RunState = runState, UpdatedAtUtc = nowIso });
                setting.UpdatedAtUtc = nowIso;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new RetryWorkflowStepResponseData(newJobId, newRevision, runState);
    }

    private static JobMediaSnapshotDto? ReadMediaSnapshot(string? configSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(configSnapshotJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(configSnapshotJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("MediaSnapshot", out var value) && !root.TryGetProperty("mediaSnapshot", out value))
            {
                return null;
            }
            return value.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<JobMediaSnapshotDto>(value.GetRawText(), JsonOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Prompt gốc đã gửi: <c>PromptSnapshotText</c> là JSON của PromptRenderSnapshotDto, chỉ lấy RenderedText.</summary>
    private static string? ReadRenderedPrompt(string? promptSnapshotText)
    {
        if (string.IsNullOrWhiteSpace(promptSnapshotText)) return null;
        try
        {
            using var document = JsonDocument.Parse(promptSnapshotText);
            var root = document.RootElement;
            if (!root.TryGetProperty("renderedText", out var value) && !root.TryGetProperty("RenderedText", out value))
            {
                return null;
            }
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Đầu vào lưu trong snapshot phải còn là Asset đang liên kết. Thiếu thì báo chặn, không tự tạo lại bước trước.
    /// Kiểm tệp vật lý thuộc adapter provider lúc chạy.
    /// </summary>
    private static async Task EnsureRetryInputsAvailableAsync(
        FrayloDbContext dbContext, JobMediaSnapshotDto? media, CancellationToken cancellationToken)
    {
        if (media == null) return;

        var assetIds = new[] { media.ModelAssetId, media.ProductAssetId, media.ReadyImageAssetId, media.SourceImageAssetId, media.AudioAssetId, media.ReferenceVideoAssetId }
            .Concat(media.ImageAssetIds ?? Array.Empty<string>())
            .Concat(media.MediaRoles?.Values ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct()
            .ToList();
        if (assetIds.Count == 0) return;

        var linked = await dbContext.Assets.AsNoTracking()
            .Where(a => assetIds.Contains(a.Id) && a.Status == "Linked")
            .Select(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var missing = assetIds.Except(linked).ToList();
        if (missing.Count > 0)
        {
            throw new ProjectWorkflowValidationException(
                $"Không thể thử lại vì {missing.Count} tệp đầu vào của bước đã bị gỡ hoặc không còn. Hãy bổ sung tệp hoặc bắt đầu lại.");
        }
    }
}
