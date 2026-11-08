using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Jobs;
using Fraylo.Application.Content.Workflows;
using Fraylo.Application.Prompts;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Prompts;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using static Fraylo.Application.Content.Workflows.WorkflowSceneJobs;

namespace Fraylo.Application.Content;

public sealed partial class ProjectWorkflowService
{
    private static async Task SaveOrUpdateContentItemAsync(
        FrayloDbContext dbContext,
        string projectId,
        VideoContentItemDto item,
        CancellationToken ct)
    {
        var contentKey = $"project.video_content.{projectId}";
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == contentKey, ct).ConfigureAwait(false);
        var list = new List<VideoContentItemDto>();
        if (setting != null && !string.IsNullOrWhiteSpace(setting.Value))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<List<VideoContentItemDto>>(setting.Value, JsonOptions);
                if (existing != null) list = existing;
            }
            catch { }
        }

        // Phản hồi không có nội dung TikTok (vd. job VideoPrompt) không được xóa nội dung đã có từ bước Script.
        if (string.IsNullOrWhiteSpace(item.TikTokContent))
        {
            var previous = list.FirstOrDefault(x => x.Id == item.Id || (x.SceneId != null && x.SceneId == item.SceneId));
            if (previous is not null && !string.IsNullOrWhiteSpace(previous.TikTokContent))
                item = item with { TikTokContent = previous.TikTokContent };
        }

        list.RemoveAll(x => x.Id == item.Id || (x.SceneId != null && x.SceneId == item.SceneId));
        list.Add(item);

        var json = JsonSerializer.Serialize(list, JsonOptions);
        if (setting == null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = contentKey, Value = json, UpdatedAtUtc = item.UpdatedAtUtc });
        }
        else
        {
            setting.Value = json;
            setting.UpdatedAtUtc = item.UpdatedAtUtc;
        }
    }

    private static async Task UpdateContentItemVideoStatusAsync(
        FrayloDbContext dbContext,
        string projectId,
        string? sceneId,
        string? videoAssetId,
        string status,
        string nowIso,
        CancellationToken ct)
    {
        var contentKey = $"project.video_content.{projectId}";
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == contentKey, ct).ConfigureAwait(false);
        if (setting != null && !string.IsNullOrWhiteSpace(setting.Value))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<VideoContentItemDto>>(setting.Value, JsonOptions);
                if (list != null)
                {
                    bool updated = false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].SceneId == sceneId || list[i].Id == sceneId)
                        {
                            list[i] = list[i] with
                            {
                                VideoAssetId = videoAssetId ?? list[i].VideoAssetId,
                                Status = status,
                                UpdatedAtUtc = nowIso
                            };
                            updated = true;
                        }
                    }
                    if (updated)
                    {
                        setting.Value = JsonSerializer.Serialize(list, JsonOptions);
                        setting.UpdatedAtUtc = nowIso;
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Lấy danh sách nội dung kịch bản và prompt của Dự án dạng bảng để người dùng xem và quyết định tạo video.
    /// </summary>
    public async Task<IReadOnlyList<VideoContentItemDto>> ListVideoContentItemsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var contentKey = $"project.video_content.{normalizedProjectId}";
        var setting = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == contentKey, cancellationToken).ConfigureAwait(false);

        var list = new List<VideoContentItemDto>();
        if (setting != null && !string.IsNullOrWhiteSpace(setting.Value))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<List<VideoContentItemDto>>(setting.Value, JsonOptions);
                if (stored != null) list = stored;
            }
            catch { }
        }

        // Đồng bộ dữ liệu mới nhất từ Scene (VideoAssetId, ImageAssetId, Name, PromptText)
        var scenes = await dbContext.Scenes
            .AsNoTracking()
            .Where(s => s.ProjectId == normalizedProjectId)
            .OrderBy(s => s.SequenceOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (list.Count == 0 && scenes.Count > 0)
        {
            // Tự sinh các mục nội dung từ Scenes nếu chưa có AppSetting
            foreach (var sc in scenes)
            {
                list.Add(new VideoContentItemDto(
                    Id: sc.Id,
                    ProjectId: sc.ProjectId,
                    SceneId: sc.Id,
                    SequenceOrder: sc.SequenceOrder,
                    Idea: sc.Name,
                    Hook: string.Empty,
                    VideoScript: sc.PromptText ?? string.Empty,
                    VideoPrompt: sc.PromptText ?? string.Empty,
                    TikTokContent: string.Empty,
                    ImageAssetId: sc.ImageAssetId,
                    VideoAssetId: sc.VideoAssetId,
                    AudioAssetId: sc.AudioAssetId,
                    Status: !string.IsNullOrWhiteSpace(sc.VideoAssetId) ? "Completed" : "Draft",
                    CreatedAtUtc: sc.CreatedAtUtc,
                    UpdatedAtUtc: sc.UpdatedAtUtc
                ));
            }
        }
        else
        {
            // Cập nhật lại VideoAssetId nếu Scene đã có video mới
            for (int i = 0; i < list.Count; i++)
            {
                var sc = scenes.FirstOrDefault(s => s.Id == list[i].SceneId || s.Id == list[i].Id);
                if (sc != null)
                {
                    list[i] = list[i] with
                    {
                        VideoPrompt = sc.PromptText ?? string.Empty,
                        ImageAssetId = sc.ImageAssetId,
                        AudioAssetId = sc.AudioAssetId,
                        VideoAssetId = sc.VideoAssetId,
                        Status = !string.IsNullOrWhiteSpace(sc.VideoAssetId) ? "Completed" : list[i].Status
                    };
                }
            }
        }

        // Trạng thái Queued/InProgress lưu lúc đưa vào hàng đợi; job render đã Failed/Canceled thì không được kẹt ở "Đang xử lý".
        var renderJobs = await dbContext.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == normalizedProjectId && j.TaskType == JobTaskType.VideoRender && j.SceneId != null)
            .Select(j => new { j.SceneId, j.State, j.CreatedAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var latestRenderState = renderJobs
            .GroupBy(j => j.SceneId!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAtUtc, StringComparer.Ordinal).First().State);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Status is not ("Queued" or "InProgress") || !string.IsNullOrWhiteSpace(list[i].VideoAssetId)) continue;
            var sceneKey = list[i].SceneId ?? list[i].Id;
            if (!latestRenderState.TryGetValue(sceneKey, out var jobState)) continue;
            if (jobState is JobState.Failed or JobState.Canceled) list[i] = list[i] with { Status = "Failed" };
        }

        // Nội dung Script và prompt ảnh lấy từ Scene hiện tại, không lưu bản sao có thể lệch khi Scene đổi.
        var planKeys = scenes.Select(s => $"scene.plan.{s.Id}").ToArray();
        var plans = await dbContext.AppSettings.AsNoTracking()
            .Where(s => planKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken).ConfigureAwait(false);
        // Chế độ 9 ô: prompt ảnh thật là mô tả hình của từng ô gửi kèm mẫu Image; scene.plan chỉ là mô tả + chuyển động cho prompt video.
        var storyboard = await new StoryboardContentService(_dbOptions).GetAsync(normalizedProjectId, cancellationToken).ConfigureAwait(false);
        string? gridImagePrompt = null;
        if (storyboard is { } sb
            && sb.Shots.Count == ReferenceImageModes.GridShotCount
            && sb.Shots.All(shot => !string.IsNullOrWhiteSpace(shot.ImagePrompt)))
        {
            try
            {
                var template = await VideoWorkflowPromptComposer.RequireActiveTemplateAsync(dbContext, JobTaskType.Image, cancellationToken).ConfigureAwait(false);
                var rendered = (await new PromptTemplateService(dbContext).PreviewAsync(
                    new PromptPreviewRequest(template.Id, null, null, null), cancellationToken).ConfigureAwait(false)).RenderedText;
                gridImagePrompt = VideoWorkflowPromptComposer.ComposeGridImagePrompt(rendered, sb);
            }
            catch (ProjectWorkflowValidationException)
            {
                // Chưa chọn mẫu Image: giữ nội dung scene.plan.
            }
        }
        for (int i = 0; i < list.Count; i++)
        {
            var sc = scenes.FirstOrDefault(s => s.Id == list[i].SceneId || s.Id == list[i].Id);
            if (sc == null) continue;
            list[i] = list[i] with
            {
                ContentText = sc.Name,
                ImagePrompt = gridImagePrompt ?? plans.GetValueOrDefault($"scene.plan.{sc.Id}")
            };
        }

        return list;
    }

    /// <summary>
    /// Cập nhật nội dung hàng kịch bản (khi người dùng chỉnh sửa trong bảng trước khi tạo video).
    /// </summary>
    public async Task<VideoContentItemDto> UpdateVideoContentItemAsync(
        UpdateVideoContentItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var items = await ListVideoContentItemsAsync(normalizedProjectId, cancellationToken).ConfigureAwait(false);
        var existing = items.FirstOrDefault(i => i.Id == request.Id || i.SceneId == request.Id);

        if (existing == null)
        {
            throw new ProjectWorkflowValidationException($"Không tìm thấy mục kịch bản '{request.Id}'.");
        }

        var nowIso = DateTimeOffset.UtcNow.ToString("o");
        var updated = existing with
        {
            Idea = request.Idea ?? existing.Idea,
            Hook = request.Hook ?? existing.Hook,
            VideoScript = request.VideoScript ?? existing.VideoScript,
            VideoPrompt = request.VideoPrompt ?? existing.VideoPrompt,
            TikTokContent = request.TikTokContent ?? existing.TikTokContent,
            UpdatedAtUtc = nowIso
        };

        await SaveOrUpdateContentItemAsync(dbContext, normalizedProjectId, updated, cancellationToken).ConfigureAwait(false);

        // Đồng bộ cập nhật sang Scene tương ứng
        var scene = await dbContext.Scenes.FirstOrDefaultAsync(s => s.Id == updated.SceneId || s.Id == updated.Id, cancellationToken).ConfigureAwait(false);
        if (scene != null)
        {
            var promptChanged = !string.Equals(scene.PromptText, updated.VideoPrompt, StringComparison.Ordinal);
            scene.Name = updated.Idea;
            scene.PromptText = updated.VideoPrompt;
            scene.UpdatedAtUtc = nowIso;
            // Prompt là đầu vào của render: video đã có không còn đại diện nội dung hiện tại nên phải xem lại trước khi đăng
            if (promptChanged)
            {
                await MarkSceneVideoStaleAsync(dbContext, scene.Id, "Prompt video đã thay đổi.", nowIso, cancellationToken).ConfigureAwait(false);
            }
            // Người dùng lưu prompt hợp lệ thì thay thế prompt bị AI từ chối: bỏ cờ lỗi prompt, giữ cờ video cần tạo lại.
            if (!string.IsNullOrWhiteSpace(request.VideoPrompt))
            {
                await ClearStaleVideoPromptAsync(dbContext, scene.Id, nowIso, cancellationToken).ConfigureAwait(false);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>Chặn render khi có phân cảnh còn cờ prompt video bị AI từ chối, kể cả khi prompt cũ vẫn còn trong Scene.</summary>
    private static async Task EnsureScenePromptsRenderableAsync(
        FrayloDbContext dbContext, IEnumerable<string> sceneIds, CancellationToken cancellationToken)
    {
        var keys = sceneIds.Select(GetSceneKey).ToList();
        var settings = await dbContext.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (settings.Any(s => IsPromptStale(s.Value)))
        {
            throw new ProjectWorkflowValidationException("Có phân cảnh cần xem lại prompt video. Hãy sửa hoặc tạo lại prompt trong bảng Project trước khi render.");
        }
    }

    /// <summary>Đánh dấu video của cảnh cần xem lại; FindReadyVideosAsync không cho đăng video đã stale.</summary>
    private static async Task MarkSceneVideoStaleAsync(
        FrayloDbContext dbContext, string sceneId, string reason, string nowIso, CancellationToken cancellationToken)
    {
        var key = GetSceneKey(sceneId);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value)) return;

        var meta = JsonSerializer.Deserialize<SceneWorkflowDetailDto>(setting.Value, JsonOptions);
        if (meta is null) return;

        var staleTasks = new HashSet<string>(meta.StaleTaskTypes ?? Array.Empty<string>(), StringComparer.Ordinal) { JobTaskType.VideoRender };
        meta = meta with { IsStale = true, StaleReason = reason, StaleTaskTypes = staleTasks.ToArray() };
        setting.Value = JsonSerializer.Serialize(meta, JsonOptions);
        setting.UpdatedAtUtc = nowIso;
    }

    /// <summary>Bỏ cờ lỗi prompt video sau khi người dùng lưu prompt hợp lệ; cờ VideoRender vẫn giữ nguyên.</summary>
    private static async Task ClearStaleVideoPromptAsync(
        FrayloDbContext dbContext, string sceneId, string nowIso, CancellationToken cancellationToken)
    {
        var key = GetSceneKey(sceneId);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value)) return;

        var meta = JsonSerializer.Deserialize<SceneWorkflowDetailDto>(setting.Value, JsonOptions);
        if (meta is null) return;

        var staleTasks = (meta.StaleTaskTypes ?? Array.Empty<string>()).Where(t => t != JobTaskType.VideoPrompt).ToArray();
        meta = meta with { IsStale = staleTasks.Length > 0, StaleReason = staleTasks.Length > 0 ? meta.StaleReason : null, StaleTaskTypes = staleTasks };
        setting.Value = JsonSerializer.Serialize(meta, JsonOptions);
        setting.UpdatedAtUtc = nowIso;
    }

    /// <summary>
    /// Kích hoạt tạo video cho một hàng trong bảng (Action Tạo video).
    /// Đưa tác vụ VideoRender vào queue, gửi video_prompt + ảnh mẫu mặc sản phẩm (+ audio nếu có).
    /// </summary>
    public async Task<int> CreateSceneVideoAsync(
        CreateSceneVideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));

        if (_jobService == null)
        {
            throw new ProjectWorkflowValidationException("Dịch vụ hàng đợi (JobService) chưa sẵn sàng.");
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var config = await GetWorkflowConfigAsync(normalizedProjectId, cancellationToken).ConfigureAwait(false);

        var items = await ListVideoContentItemsAsync(normalizedProjectId, cancellationToken).ConfigureAwait(false);
        var targetItem = !string.IsNullOrWhiteSpace(request.ContentItemId)
            ? items.FirstOrDefault(i => i.Id == request.ContentItemId)
            : (!string.IsNullOrWhiteSpace(request.SceneId)
                ? items.FirstOrDefault(i => i.SceneId == request.SceneId || i.Id == request.SceneId)
                : items.FirstOrDefault());

        if (targetItem == null)
        {
            throw new ProjectWorkflowValidationException("Không tìm thấy nội dung kịch bản để tạo video.");
        }

        var scene = await dbContext.Scenes.FirstOrDefaultAsync(s => s.ProjectId == normalizedProjectId && (s.Id == targetItem.SceneId || s.Id == targetItem.Id), cancellationToken).ConfigureAwait(false)
            ?? throw new ProjectWorkflowValidationException("Phân cảnh không còn tồn tại trong dự án.");
        var targetSceneId = scene.Id;
        var referenceVideo = config.WorkflowMode == WorkflowModes.ReferenceVideo;
        if (referenceVideo) VideoWorkflows.For(config.WorkflowMode).ValidateInputs(config);
        var hasOtherCompletedVideo = items.Any(i => i.Id != targetItem.Id && i.VideoAssetId != null) || await dbContext.Scenes
            .AsNoTracking()
            .AnyAsync(s => s.ProjectId == normalizedProjectId && s.Id != targetSceneId && s.VideoAssetId != null, cancellationToken)
            .ConfigureAwait(false);
        var hasOtherActiveRender = await dbContext.Jobs
            .AsNoTracking()
            .AnyAsync(j =>
                j.ProjectId == normalizedProjectId &&
                j.TaskType == JobTaskType.VideoRender &&
                j.SceneId != targetSceneId &&
                (j.State == JobState.Queued || j.State == JobState.Reserved || j.State == JobState.Running || j.State == JobState.AwaitingUser),
                cancellationToken)
            .ConfigureAwait(false);
        // Chỉ luồng một video bị giới hạn một video đầu ra; nhiều video thì mỗi cảnh render riêng.
        if (config.VideoStrategy == VideoStrategies.SingleVideo && (hasOtherCompletedVideo || hasOtherActiveRender))
        {
            throw new ProjectWorkflowValidationException(
                "Mỗi dự án chỉ có một video đầu ra. Hãy tạo lại video hiện tại thay vì tạo video cho một cảnh khác.");
        }

        var hasSameSceneActiveRender = await dbContext.Jobs.AsNoTracking().AnyAsync(j =>
            j.ProjectId == normalizedProjectId && j.SceneId == targetSceneId && j.TaskType == JobTaskType.VideoRender &&
            (j.State == JobState.Queued || j.State == JobState.Reserved || j.State == JobState.Running || j.State == JobState.AwaitingUser),
            cancellationToken).ConfigureAwait(false);
        if (hasSameSceneActiveRender)
        {
            throw new ProjectWorkflowValidationException("Video của phân cảnh này đang được tạo.");
        }

        if (!referenceVideo && string.IsNullOrWhiteSpace(scene.PromptText))
        {
            throw new ProjectWorkflowValidationException("Cảnh này chưa có prompt video. Hãy tạo prompt video trước khi tạo video.");
        }
        await EnsureScenePromptsRenderableAsync(dbContext, new[] { targetSceneId }, cancellationToken).ConfigureAwait(false);
        if (await NeedsGridImageAsync(dbContext, config, scene, cancellationToken).ConfigureAwait(false))
        {
            throw new ProjectWorkflowValidationException("Ảnh storyboard 3×3 chưa sẵn sàng nên chưa thể tạo video.");
        }

        var imageAssetId = scene.ImageAssetId;
        var audioAssetId = scene.AudioAssetId;

        var snapshot = new JobMediaSnapshotDto(
            ModelAssetId: config.ModelAssetId,
            ProductAssetId: config.ProductAssetId,
            ReadyImageAssetId: config.ReadyImageAssetId,
            SourceImageAssetId: imageAssetId,
            AudioAssetId: audioAssetId,
            DurationSeconds: scene.TargetDurationSeconds > 0 ? Convert.ToInt32(scene.TargetDurationSeconds) : config.TargetDurationPerSceneSeconds,
            AudioOption: audioAssetId != null ? AudioOptions.CustomAudio : AudioOptions.None,
            MediaRoles: new Dictionary<string, string> { ["SourceImage"] = imageAssetId ?? string.Empty },
            ImageAssetIds: string.IsNullOrWhiteSpace(imageAssetId) ? null : new[] { imageAssetId }
        );

        config = config with { CurrentRunId = config.CurrentRunId ?? Guid.NewGuid().ToString("D") };
        var renderStepKey = await BuildRenderStepKeyAsync(dbContext, normalizedProjectId, config.CurrentRunId, $"render.{targetItem.Id}", cancellationToken).ConfigureAwait(false);
        var enqueueRequest = new JobEnqueueRequest(
            ProjectId: normalizedProjectId,
            SceneId: targetSceneId,
            TaskType: JobTaskType.VideoRender,
            CustomPromptText: scene.PromptText,
            WorkflowRunId: config.CurrentRunId,
            WorkflowStepKey: renderStepKey,
            MediaSnapshot: snapshot
        );
        if (referenceVideo)
        {
            enqueueRequest = ReferenceVideoWorkflow.CreateRenderRequest(new WorkflowSceneContext(
                dbContext, normalizedProjectId, config, scene, null, new HashSet<string>(), BuildMediaSnapshot(config, scene)));
            enqueueRequest = enqueueRequest with { WorkflowStepKey = renderStepKey };
        }

        var response = await _jobService.StageWorkflowJobAsync(enqueueRequest, dbContext, cancellationToken).ConfigureAwait(false);

        if (response != null)
        {
            // Cập nhật trạng thái item thành Queued
            await UpdateContentItemVideoStatusAsync(dbContext, normalizedProjectId, targetItem.SceneId ?? targetItem.Id, null, "Queued", DateTimeOffset.UtcNow.ToString("o"), cancellationToken).ConfigureAwait(false);
            scene.Status = "Queued";
            // Job tạo lại và run phải cùng commit; scheduler không dispatch job của run đã Completed.
            var now = DateTimeOffset.UtcNow.ToString("o");
            var configKey = GetConfigKey(normalizedProjectId);
            var storedConfig = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == configKey, cancellationToken).ConfigureAwait(false);
            var updatedConfig = config with { RunState = config.RunState == WorkflowRunState.Paused ? WorkflowRunState.Paused : WorkflowRunState.Running, UpdatedAtUtc = now };
            if (storedConfig == null)
                dbContext.AppSettings.Add(new AppSetting { Key = configKey, Value = SerializeStoredConfig(updatedConfig), UpdatedAtUtc = now });
            else
            {
                storedConfig.Value = SerializeStoredConfig(updatedConfig);
                storedConfig.UpdatedAtUtc = now;
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _jobService.NotifyWorkflowRunReady();
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Thẩm định và tạo bản xem trước yêu cầu gửi AI writer (Preview AI Prompt) theo CLIP_DURATION_STORYBOARD_PLAN §3.1–3.3.
    /// Dùng chung logic composition với quá trình enqueue thật.
    /// </summary>
    public async Task<PreviewAiPromptResponseData> PreviewAiPromptAsync(
        PreviewAiPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));

        var config = await GetWorkflowConfigAsync(normalizedProjectId, cancellationToken);
        var composer = new VideoWorkflowPromptComposer(_dbOptions);

        await using var dbContext = new FrayloDbContext(_dbOptions);
        Scene? targetScene = null;
        if (!string.IsNullOrWhiteSpace(request.SceneId))
        {
            var normalizedSceneId = NormalizeAndValidateGuid(request.SceneId, nameof(request.SceneId));
            targetScene = await dbContext.Scenes
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == normalizedSceneId && s.ProjectId == normalizedProjectId, cancellationToken);
        }

        if (targetScene == null)
        {
            targetScene = await dbContext.Scenes
                .AsNoTracking()
                .Where(s => s.ProjectId == normalizedProjectId)
                .OrderBy(s => s.SequenceOrder)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (targetScene == null)
        {
            targetScene = new Scene
            {
                Id = "preview-scene-1",
                ProjectId = normalizedProjectId,
                SequenceOrder = 1,
                Name = "Clip 1",
                TargetDurationSeconds = config.TargetDurationPerSceneSeconds > 0 ? config.TargetDurationPerSceneSeconds : 15,
                ImageAssetId = config.ReadyImageAssetId ?? config.ProductAssetId ?? config.ModelAssetId,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
            };
        }

        var composition = await composer.ComposeVideoPromptAiRequestAsync(
            config,
            targetScene,
            cancellationToken);

        return new PreviewAiPromptResponseData(
            TemplateId: composition.TemplateId,
            TemplateName: composition.TemplateName,
            ComposedPrompt: composition.ComposedPrompt,
            OutputContractVersion: composition.OutputContractVersion
        );
    }

    /// <summary>
    /// Khóa step theo số lần render trong run: bấm Tạo lại sau job đã kết thúc sẽ tạo job mới,
    /// còn bấm lặp khi job trước chưa kết thúc vẫn bị idempotency và kiểm tra job đang chạy chặn.
    /// </summary>
    private async Task<string> BuildRenderStepKeyAsync(
        FrayloDbContext dbContext, string projectId, string? runId, string baseKey, CancellationToken cancellationToken)
    {
        var snapshots = await dbContext.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.TaskType == JobTaskType.VideoRender)
            .Select(j => j.ConfigSnapshotJson)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var attempts = snapshots.Count(s => GetWorkflowRunId(s) == runId);
        return $"{baseKey}.{attempts + 1}";
    }

}
