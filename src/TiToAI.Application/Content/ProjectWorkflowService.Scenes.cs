using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TiToAI.Application.Jobs;
using TiToAI.Application.Prompts;
using TiToAI.Contracts.Content;
using TiToAI.Contracts.Jobs;
using TiToAI.Contracts.Prompts;
using TiToAI.Contracts.Scenes;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Application.Content;

public sealed partial class ProjectWorkflowService
{
    /// <summary>
    /// Lấy danh sách chi tiết quy trình của tất cả các Cảnh trong Dự án.
    /// </summary>
    public async Task<IReadOnlyList<SceneWorkflowDetailDto>> ListSceneWorkflowDetailsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var scenes = await dbContext.Scenes
            .AsNoTracking()
            .Where(s => s.ProjectId == normalizedProjectId)
            .OrderBy(s => s.SequenceOrder)
            .ToListAsync(cancellationToken);

        var result = new List<SceneWorkflowDetailDto>();

        foreach (var s in scenes)
        {
            var key = GetSceneKey(s.Id);
            var setting = await dbContext.AppSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Key == key, cancellationToken);

            if (setting != null && !string.IsNullOrWhiteSpace(setting.Value))
            {
                try
                {
                    var detail = JsonSerializer.Deserialize<SceneWorkflowDetailDto>(setting.Value, JsonOptions);
                    if (detail != null)
                    {
                        // Luôn đồng bộ dữ liệu cốt lõi từ bảng Scene
                        detail = detail with
                        {
                            Name = s.Name,
                            SequenceOrder = s.SequenceOrder,
                            TargetDurationSeconds = Convert.ToInt32(s.TargetDurationSeconds),
                            SelectedImageAssetId = s.ImageAssetId,
                            MotionPromptText = s.PromptText,
                            AudioAssetId = s.AudioAssetId,
                            VideoAssetId = s.VideoAssetId,
                            Revision = s.UpdatedAtUtc
                        };
                        result.Add(detail);
                        continue;
                    }
                }
                catch
                {
                    // Fallback
                }
            }

            // Fallback tạo từ Scene nếu chưa có AppSetting
            string workflowStatus = !string.IsNullOrWhiteSpace(s.VideoAssetId)
                ? SceneWorkflowStatus.VideoCompleted
                : (!string.IsNullOrWhiteSpace(s.ImageAssetId) && !string.IsNullOrWhiteSpace(s.PromptText))
                    ? SceneWorkflowStatus.ReadyForVideo
                    : (!string.IsNullOrWhiteSpace(s.ImageAssetId))
                        ? SceneWorkflowStatus.PreparingPrompt
                        : SceneWorkflowStatus.PreparingImage;

            result.Add(new SceneWorkflowDetailDto(
                SceneId: s.Id,
                ProjectId: s.ProjectId,
                SequenceOrder: s.SequenceOrder,
                Name: s.Name,
                TargetDurationSeconds: Convert.ToInt32(s.TargetDurationSeconds),
                WorkflowStatus: workflowStatus,
                ImageStrategy: SceneImageStrategy.ReuseFoundation,
                SourceImageAssetId: s.ImageAssetId,
                SelectedImageAssetId: s.ImageAssetId,
                MotionPromptText: s.PromptText,
                AudioOption: s.AudioAssetId != null ? AudioOptions.CustomAudio : AudioOptions.None,
                AudioAssetId: s.AudioAssetId,
                VideoAssetId: s.VideoAssetId,
                IsStale: false,
                StaleReason: null,
                ActiveJobId: null,
                ActiveJobType: null,
                ActiveJobState: null,
                ActiveJobProgressPercent: null,
                VideoCandidateHistory: null,
                Revision: s.UpdatedAtUtc
            ));
        }

        return result;
    }

    /// <summary>
    /// Cập nhật đầu vào của Cảnh (Override) và áp dụng ma trận phụ thuộc §3.4:
    /// - Đổi ảnh -> Đánh dấu làm lại prompt và video; không sinh lại ảnh vừa chọn.
    /// - Đổi prompt -> Chỉ làm lại video cảnh đó; giữ ảnh.
    /// - Đổi audio -> Chỉ làm lại video cảnh đó.
    /// </summary>
    public async Task<SceneWorkflowDetailDto> UpdateSceneWorkflowInputAsync(
        UpdateSceneWorkflowInputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));
        var normalizedSceneId = NormalizeAndValidateGuid(request.SceneId, nameof(request.SceneId));

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var scene = await dbContext.Scenes
            .FirstOrDefaultAsync(s => s.Id == normalizedSceneId && s.ProjectId == normalizedProjectId, cancellationToken);

        if (scene == null)
        {
            throw new ProjectWorkflowValidationException($"Không tìm thấy Cảnh '{normalizedSceneId}'.");
        }

        var workflowConfig = await GetWorkflowConfigAsync(normalizedProjectId, cancellationToken).ConfigureAwait(false);
        if (workflowConfig.RunState == WorkflowRunState.Running)
        {
            throw new ProjectWorkflowValidationException("Tạm dừng run trước khi thay đổi input của một cảnh.");
        }
        if (workflowConfig.RunState == WorkflowRunState.Paused)
        {
            bool hasActiveSceneJob = await dbContext.Jobs.AsNoTracking().AnyAsync(job =>
                job.SceneId == scene.Id &&
                (job.State == JobState.Queued || job.State == JobState.Reserved || job.State == JobState.Running || job.State == JobState.AwaitingUser),
                cancellationToken).ConfigureAwait(false);
            if (hasActiveSceneJob)
            {
                throw new ProjectWorkflowValidationException("Đợi các job đang hoạt động hoặc chưa được đối soát xong trước khi sửa input của cảnh.");
            }
        }

        // Optimistic concurrency check
        if (!string.IsNullOrWhiteSpace(request.ExpectedRevision) && scene.UpdatedAtUtc != request.ExpectedRevision)
        {
            throw new SceneConcurrencyConflictException(scene.Id, request.ExpectedRevision, scene.UpdatedAtUtc);
        }

        var originalAudioAssetId = scene.AudioAssetId;
        var originalDuration = Convert.ToInt32(scene.TargetDurationSeconds);
        var nowIso = DateTimeOffset.UtcNow.ToString("o");
        bool imageChanged = false;
        bool promptChanged = false;

        // 1. Kiểm tra thay đổi ảnh
        if (request.SelectedImageAssetId != null && request.SelectedImageAssetId != scene.ImageAssetId)
        {
            if (!string.IsNullOrWhiteSpace(request.SelectedImageAssetId))
            {
                await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.SelectedImageAssetId, "ảnh được chọn", cancellationToken);
            }
            scene.ImageAssetId = string.IsNullOrWhiteSpace(request.SelectedImageAssetId) ? null : request.SelectedImageAssetId;
            imageChanged = true;
        }

        // 2. Kiểm tra thay đổi prompt
        if (request.MotionPromptText != null && request.MotionPromptText != scene.PromptText)
        {
            scene.PromptText = request.MotionPromptText;
            promptChanged = true;
        }

        // 3. Thay đổi audio
        if (request.AudioOption != null)
        {
            if (request.AudioOption == AudioOptions.CustomAudio && request.AudioAssetId != null)
            {
                await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.AudioAssetId, "tệp âm thanh", cancellationToken);
                scene.AudioAssetId = request.AudioAssetId;
            }
            else if (request.AudioOption == AudioOptions.None)
            {
                scene.AudioAssetId = null;
            }
        }

        if (request.TargetDurationSeconds.HasValue && request.TargetDurationSeconds.Value > 0)
        {
            scene.TargetDurationSeconds = request.TargetDurationSeconds.Value;
        }

        bool audioChanged = scene.AudioAssetId != originalAudioAssetId;
        bool durationChanged = Convert.ToInt32(scene.TargetDurationSeconds) != originalDuration;

        scene.UpdatedAtUtc = nowIso;

        // Áp dụng ma trận phụ thuộc §3.4:
        // Nếu thay ảnh: Prompt và Video cũ không còn đại diện cấu hình hiện tại -> đánh dấu stale
        // Nếu thay prompt: Video cũ không còn đại diện -> đánh dấu stale
        string currentWorkflowStatus = !string.IsNullOrWhiteSpace(scene.VideoAssetId)
            ? SceneWorkflowStatus.VideoCompleted
            : (!string.IsNullOrWhiteSpace(scene.ImageAssetId) && !string.IsNullOrWhiteSpace(scene.PromptText))
                ? SceneWorkflowStatus.ReadyForVideo
                : SceneWorkflowStatus.PreparingImage;

        var key = GetSceneKey(scene.Id);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        SceneWorkflowDetailDto? existingSceneMeta = null;
        if (setting != null && !string.IsNullOrWhiteSpace(setting.Value))
        {
            try { existingSceneMeta = JsonSerializer.Deserialize<SceneWorkflowDetailDto>(setting.Value, JsonOptions); } catch { }
        }
        var candidateHistory = existingSceneMeta?.VideoCandidateHistory;
        var staleTasks = new HashSet<string>(existingSceneMeta?.StaleTaskTypes ?? Array.Empty<string>(), StringComparer.Ordinal);
        // Prompt người dùng nhập thay thế prompt bị AI từ chối: bỏ cờ lỗi prompt, các cờ do ảnh/thời lượng/âm thanh vẫn được áp dụng bên dưới.
        if (promptChanged && !string.IsNullOrWhiteSpace(scene.PromptText))
        {
            staleTasks.Remove(JobTaskType.VideoPrompt);
        }
        if (imageChanged)
        {
            staleTasks.Add(JobTaskType.VideoPrompt);
            staleTasks.Add(JobTaskType.VideoRender);
        }
        if (promptChanged)
        {
            staleTasks.Add(JobTaskType.VideoRender);
        }
        if (audioChanged || durationChanged)
        {
            staleTasks.Add(JobTaskType.VideoPrompt);
            staleTasks.Add(JobTaskType.VideoRender);
        }

        bool isStale = staleTasks.Count > 0;
        string? staleReason = imageChanged ? "Ảnh dùng cho video đã thay đổi. Cần tạo lại prompt và video." :
                              promptChanged ? "Prompt chuyển động đã thay đổi. Cần tạo lại video." :
                              durationChanged ? "Thời lượng clip đã thay đổi. Cần tạo lại prompt và video." :
                              audioChanged ? "Âm thanh đã thay đổi. Cần tạo lại prompt và video." :
                              existingSceneMeta?.StaleReason;
        if (isStale)
        {
            currentWorkflowStatus = SceneWorkflowStatus.NeedsReview;
        }

        var sceneDetail = new SceneWorkflowDetailDto(
            SceneId: scene.Id,
            ProjectId: scene.ProjectId,
            SequenceOrder: scene.SequenceOrder,
            Name: scene.Name,
            TargetDurationSeconds: Convert.ToInt32(scene.TargetDurationSeconds),
            WorkflowStatus: currentWorkflowStatus,
            ImageStrategy: request.ImageStrategy ?? SceneImageStrategy.ReuseFoundation,
            SourceImageAssetId: scene.ImageAssetId,
            SelectedImageAssetId: scene.ImageAssetId,
            MotionPromptText: scene.PromptText,
            AudioOption: scene.AudioAssetId != null ? AudioOptions.CustomAudio : AudioOptions.None,
            AudioAssetId: scene.AudioAssetId,
            VideoAssetId: scene.VideoAssetId,
            IsStale: isStale,
            StaleReason: staleReason,
            ActiveJobId: null,
            ActiveJobType: null,
            ActiveJobState: null,
            ActiveJobProgressPercent: null,
            VideoCandidateHistory: candidateHistory,
            Revision: nowIso,
            StaleTaskTypes: staleTasks.ToArray()
        );

        var jsonVal = JsonSerializer.Serialize(sceneDetail, JsonOptions);

        if (setting == null)
        {
            dbContext.AppSettings.Add(new AppSetting
            {
                Key = key,
                Value = jsonVal,
                UpdatedAtUtc = nowIso
            });
        }
        else
        {
            setting.Value = jsonVal;
            setting.UpdatedAtUtc = nowIso;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return sceneDetail;
    }

    /// <summary>
    /// Enqueue các tác vụ độc lập cho các cảnh đủ điều kiện chạy ngay (§3.3).
    /// </summary>
    public async Task<int> EnqueueJobsForExistingScenesAsync(
        TiToAIDbContext dbContext,
        string projectId,
        ProjectWorkflowConfigDto config,
        List<Scene> scenes,
        CancellationToken cancellationToken,
        bool deferDispatch = false,
        bool stageWithRun = false)
    {
        if (_jobService == null) return 0;
        int count = 0;
        var composer = new VideoWorkflowPromptComposer(_dbOptions);

        foreach (var scene in scenes)
        {
            var planKey = $"scene.plan.{scene.Id}";
            var scenePurpose = dbContext.AppSettings.Local.FirstOrDefault(setting => setting.Key == planKey)?.Value
                ?? await dbContext.AppSettings.AsNoTracking().Where(setting => setting.Key == planKey)
                    .Select(setting => setting.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            // Tự gán ảnh cho ReadyImage / ProductOnly nếu cảnh chưa có
            if (string.IsNullOrWhiteSpace(scene.ImageAssetId))
            {
                if (config.WorkflowMode == WorkflowModes.ReadyImage && !string.IsNullOrWhiteSpace(config.ReadyImageAssetId))
                {
                    scene.ImageAssetId = config.ReadyImageAssetId;
                }
                else if (config.WorkflowMode == WorkflowModes.ProductOnly && config.VideoStrategy == VideoStrategies.SingleVideo && !string.IsNullOrWhiteSpace(config.ProductAssetId))
                {
                    scene.ImageAssetId = config.ProductAssetId;
                }
            }

            var snapshot = BuildMediaSnapshot(config, scene);
            SceneWorkflowDetailDto? sceneMeta = null;
            var sceneSetting = await dbContext.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(setting => setting.Key == GetSceneKey(scene.Id), cancellationToken)
                .ConfigureAwait(false);
            if (sceneSetting != null)
            {
                try { sceneMeta = JsonSerializer.Deserialize<SceneWorkflowDetailDto>(sceneSetting.Value, JsonOptions); } catch { }
            }
            var staleTasks = new HashSet<string>(sceneMeta?.StaleTaskTypes ?? Array.Empty<string>(), StringComparer.Ordinal);
            JobEnqueueRequest? nextRequest = null;

            // 1. Cảnh chưa có ảnh và ở chế độ ModelAndProduct: Enqueue Job Image
            if ((staleTasks.Contains(JobTaskType.Image) || string.IsNullOrWhiteSpace(scene.ImageAssetId)) &&
                CanGenerateImage(config.WorkflowMode, config.VideoStrategy))
            {
                var prompt = scenePurpose ?? scene.PromptText ?? config.GoalPrompt;
                var imageTemplate = await dbContext.PromptTemplates.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Purpose == JobTaskType.Image && t.IsActive, cancellationToken).ConfigureAwait(false);
                var imageInstruction = imageTemplate == null ? string.Empty :
                    (await new PromptTemplateService(dbContext).PreviewAsync(
                        new PromptPreviewRequest(imageTemplate.Id, null, null, null), cancellationToken).ConfigureAwait(false)).RenderedText + "\n\n";
                nextRequest = new JobEnqueueRequest(
                    ProjectId: projectId,
                    SceneId: scene.Id,
                    TaskType: JobTaskType.Image,
                    CustomPromptText: imageInstruction + (config.WorkflowMode == WorkflowModes.ProductOnly
                        ? $"Tạo một ảnh sản phẩm cho cảnh: {scene.Name}. Ảnh đính kèm là sản phẩm cần giữ đúng màu sắc, hình dáng và chi tiết nhìn thấy. {prompt}"
                        : $"Tạo một ảnh người mẫu mặc sản phẩm cho cảnh: {scene.Name}. Ảnh đính kèm thứ nhất là người mẫu cần giữ nhận diện; ảnh thứ hai là sản phẩm cần mặc, giữ đúng màu sắc và chi tiết nhìn thấy. {prompt}"),
                    WorkflowRunId: config.CurrentRunId,
                    WorkflowStepKey: $"image.{scene.Id}.{scene.UpdatedAtUtc}",
                    MediaSnapshot: snapshot
                );
            }
            // 2. Cảnh đã có ảnh nhưng chưa có PromptText chuyển động: Enqueue Job VideoPrompt
            // Nối dữ liệu qua VideoWorkflowPromptComposer: Template VideoPrompt đã render + Clip Context
            else if (!string.IsNullOrWhiteSpace(scene.ImageAssetId) &&
                     (staleTasks.Contains(JobTaskType.VideoPrompt) || string.IsNullOrWhiteSpace(scene.PromptText)))
            {
                var composition = await composer.ComposeVideoPromptAiRequestAsync(config, scene, cancellationToken, scenePurpose).ConfigureAwait(false);

                nextRequest = new JobEnqueueRequest(
                    ProjectId: projectId,
                    SceneId: scene.Id,
                    TaskType: JobTaskType.VideoPrompt,
                    CustomPromptText: composition.ComposedPrompt,
                    WorkflowRunId: config.CurrentRunId,
                    WorkflowStepKey: $"prompt.{scene.Id}.{scene.UpdatedAtUtc}",
                    MediaSnapshot: snapshot
                );
            }
            // Stop at ReadyForVideo. VideoRender is enabled only when the real adapter is ready.

            if (nextRequest != null)
            {
                if (deferDispatch)
                {
                    AddContinuationIntent(dbContext, nextRequest, DateTimeOffset.UtcNow.ToString("o"));
                }
                else if (stageWithRun)
                {
                    await _jobService.StageWorkflowJobAsync(nextRequest, dbContext, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _jobService.EnqueueAsync(nextRequest, cancellationToken).ConfigureAwait(false);
                }
                count++;
            }
        }

        if (!stageWithRun) await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    private static void AddContinuationIntent(TiToAIDbContext dbContext, JobEnqueueRequest request, string nowIso)
    {
        var intentId = Guid.NewGuid().ToString("N");
        dbContext.AppSettings.Add(new AppSetting
        {
            Key = $"workflow.continuation.{intentId}",
            Value = JsonSerializer.Serialize(new WorkflowContinuationIntent(
                IntentId: intentId,
                ProjectId: request.ProjectId,
                SceneId: request.SceneId,
                TaskType: request.TaskType,
                CustomPromptText: request.CustomPromptText,
                MediaSnapshot: request.MediaSnapshot,
                RunId: request.WorkflowRunId ?? string.Empty,
                CreatedAtUtc: nowIso,
                WorkflowStepKey: request.WorkflowStepKey), JsonOptions),
            UpdatedAtUtc = nowIso
        });
    }
}
