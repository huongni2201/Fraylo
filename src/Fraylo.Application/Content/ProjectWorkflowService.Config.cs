using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Content.Workflows;
using Fraylo.Application.Assets;
using Fraylo.Application.Prompts;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Scenes;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Content;

public sealed partial class ProjectWorkflowService
{
    private static JobMediaSnapshotDto BuildMediaSnapshot(
        ProjectWorkflowConfigDto config,
        Scene? scene = null)
    {
        var modelId = config.ModelAssetId;
        var productId = config.ProductAssetId;
        var readyId = config.ReadyImageAssetId;
        var sourceImageId = scene?.ImageAssetId ?? readyId ?? productId ?? modelId;
        var audioId = scene?.AudioAssetId ?? (config.AudioOption == AudioOptions.CustomAudio ? config.AudioAssetId : null);
        var duration = scene?.TargetDurationSeconds > 0 ? Convert.ToInt32(scene.TargetDurationSeconds) : config.TargetDurationPerSceneSeconds;
        var audioOpt = scene?.AudioAssetId != null ? AudioOptions.CustomAudio : config.AudioOption;

        var roles = new Dictionary<string, string>();
        if (modelId != null) roles["Model"] = modelId;
        if (productId != null) roles["Product"] = productId;
        if (readyId != null) roles["ReadyImage"] = readyId;
        if (sourceImageId != null) roles["SourceImage"] = sourceImageId;
        if (audioId != null) roles["Audio"] = audioId;

        return new JobMediaSnapshotDto(
            ModelAssetId: modelId,
            ProductAssetId: productId,
            ReadyImageAssetId: readyId,
            SourceImageAssetId: sourceImageId,
            AudioAssetId: audioId,
            DurationSeconds: duration,
            AudioOption: audioOpt,
            MediaRoles: roles
        );
    }

    /// <summary>
    /// Lấy cấu hình quy trình sản xuất video cấp Dự án với <b>trạng thái hiển thị</b>: RunState được suy từ queue
    /// (vd: Failed khi mọi bước đã dừng mà còn bước lỗi/bị loại/hủy). Nếu chưa có, trả về giá trị mặc định hợp lệ.
    /// </summary>
    public Task<ProjectWorkflowConfigDto> GetWorkflowConfigAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        LoadWorkflowConfigAsync(projectId, deriveRunState: true, includeSteps: false, cancellationToken);

    /// <summary>Như <see cref="GetWorkflowConfigAsync"/> và kèm <c>Steps</c> (lần thử hiện hành từng bước, quyền retry). Chỉ dùng cho bridge.</summary>
    public Task<ProjectWorkflowConfigDto> GetWorkflowConfigWithStepsAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        LoadWorkflowConfigAsync(projectId, deriveRunState: true, includeSteps: true, cancellationToken);

    /// <summary>
    /// Cấu hình với <b>trạng thái điều khiển</b>: RunState đúng như đã lưu, không suy. Pipeline (apply, continuation,
    /// auto-render, recovery) và các thao tác ghi trạng thái dùng bản này, để một bước lỗi không làm cảnh khác bỏ việc.
    /// </summary>
    private Task<ProjectWorkflowConfigDto> GetStoredWorkflowConfigAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        LoadWorkflowConfigAsync(projectId, deriveRunState: false, includeSteps: false, cancellationToken);

    private async Task<ProjectWorkflowConfigDto> LoadWorkflowConfigAsync(
        string projectId,
        bool deriveRunState,
        bool includeSteps,
        CancellationToken cancellationToken)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var project = await dbContext.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == normalizedProjectId, cancellationToken);

        if (project == null)
        {
            throw new ProjectWorkflowValidationException($"Không tìm thấy dự án '{normalizedProjectId}'.");
        }

        // Video cuối của Project là revision đang được chọn; UI dựa vào đây thay vì số cảnh.
        var finalVideoAssetId = project.CurrentVideoRevisionId is null ? null : await dbContext.VideoRevisions
            .AsNoTracking()
            .Where(v => v.Id == project.CurrentVideoRevisionId && v.ProjectId == normalizedProjectId && v.State == "Ready")
            .Select(v => v.AssetId)
            .FirstOrDefaultAsync(cancellationToken);

        var stored = await LoadStoredConfigAsync(dbContext, normalizedProjectId, cancellationToken);
        // Queue là nguồn trạng thái hiển thị: kể cả khi bỏ lỡ sự kiện lỗi hoặc mở lại app sau timeout.
        IReadOnlyList<WorkflowStepStateDto>? steps = null;
        if (deriveRunState &&
            stored.RunState is WorkflowRunState.Running or WorkflowRunState.Paused or WorkflowRunState.Failed &&
            stored.CurrentRunId != null)
        {
            var run = await BuildRunStepsAsync(dbContext, stored, includeSteps, cancellationToken);
            stored = stored with { RunState = DeriveDisplayRunState(stored.RunState, run) };
            if (includeSteps)
            {
                steps = run.Steps;
            }
        }
        var availability = await ReferenceAvailabilityAsync(stored, cancellationToken).ConfigureAwait(false);
        return stored with
        {
            FinalVideoAssetId = finalVideoAssetId,
            Steps = steps,
            ReferenceVideoAvailable = availability.Available,
            ReferenceVideoUnavailableReason = availability.Reason
        };
    }

    private async Task<(bool Available, string? Reason)> ReferenceAvailabilityAsync(ProjectWorkflowConfigDto config, CancellationToken ct)
    {
        var capability = Fraylo.Application.Providers.ProviderCapabilities.GetDefaultCapability("museai", JobTaskType.VideoRender);
        if (capability?.SupportsReferenceVideoApproximation != true) return (false, "MuseAI chưa xác minh mô phỏng chuyển động từ video mẫu.");
        if (config.ReferenceVideoAssetId is null) return (true, null);
        try
        {
            await using var db = new FrayloDbContext(_dbOptions);
            var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == config.ReferenceVideoAssetId && a.AssetType == "Video" && a.Status == "Linked" && (a.ProjectId == null || a.ProjectId == config.ProjectId), ct);
            if (asset is null) return (false, "Video mẫu không còn hoặc không thuộc dự án.");
            var root = await new Fraylo.Infrastructure.Storage.StorageRootRegistry(_dbOptions).ResolveRootPathAsync(asset.StorageRootId, ct);
            var path = Fraylo.Infrastructure.Storage.StoragePathGuard.CombineAndVerify(root, asset.RelativePath);
            if (ReferenceVideoAudio.HasAudio(path) && !capability.SupportsReferenceVideoAudioFit)
                return (false, "MuseAI chưa xác minh tự ghép/co giãn âm thanh từ mẫu. Bạn có thể lưu nháp hoặc chọn video mẫu không có âm thanh.");
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or AssetValidationException or KeyNotFoundException or System.Security.SecurityException)
        { return (false, "Không thể đọc video mẫu: kiểm tra tệp và thư mục lưu trữ."); }
    }

    /// <summary>
    /// Cấu hình đã lưu của Dự án. Chưa có thì trả về mặc định.
    /// </summary>
    private static async Task<ProjectWorkflowConfigDto> LoadStoredConfigAsync(
        FrayloDbContext dbContext,
        string projectId,
        CancellationToken cancellationToken)
    {
        var key = GetConfigKey(projectId);
        var appSetting = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

        if (appSetting != null && !string.IsNullOrWhiteSpace(appSetting.Value))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<ProjectWorkflowConfigDto>(appSetting.Value, JsonOptions);
                if (stored != null)
                {
                    return stored.WorkflowMode == WorkflowModes.ReferenceVideo &&
                           !ReferenceVideoMedia.IsOutputDurationSupported(stored.TargetDurationPerSceneSeconds)
                        ? stored with { TargetDurationPerSceneSeconds = ReferenceVideoMedia.DefaultOutputSeconds }
                        : stored;
                }
            }
            catch
            {
                // Fallback nếu JSON hỏng
            }
        }

        // Mặc định cấu hình mới theo CLIP_DURATION_STORYBOARD_PLAN: 1 clip, ưu tiên 15 giây nếu hỗ trợ
        return new ProjectWorkflowConfigDto(
            ProjectId: projectId,
            WorkflowMode: WorkflowModes.ModelAndProduct,
            GoalPrompt: string.Empty,
            ModelAssetId: null,
            ProductAssetId: null,
            ReadyImageAssetId: null,
            FoundationImageAssetId: null,
            AudioOption: AudioOptions.None,
            AudioAssetId: null,
            TargetSceneCount: 1,
            TargetDurationPerSceneSeconds: 15,
            RunState: WorkflowRunState.Idle,
            CurrentRunId: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
            VideoStrategy: VideoStrategies.SingleVideo
        );
    }

    /// <summary>
    /// Ghi cấu hình vào AppSettings theo dạng lưu: trường tính toán (video cuối) không được ghi.
    /// </summary>
    private static string SerializeStoredConfig(ProjectWorkflowConfigDto config) =>
        JsonSerializer.Serialize(config with { FinalVideoAssetId = null, ReferenceVideoAvailable = false, ReferenceVideoUnavailableReason = null }, JsonOptions);

    /// <summary>
    /// Lưu cấu hình quy trình sản xuất video cấp Dự án.
    /// </summary>
    public async Task<ProjectWorkflowConfigDto> SaveWorkflowConfigAsync(
        SaveProjectWorkflowConfigRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));

        if (!WorkflowModes.IsValid(request.WorkflowMode))
        {
            throw new ProjectWorkflowValidationException($"Chế độ '{request.WorkflowMode}' không hợp lệ.");
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var project = await dbContext.Projects
            .FirstOrDefaultAsync(p => p.Id == normalizedProjectId, cancellationToken);

        if (project == null)
        {
            throw new ProjectWorkflowValidationException($"Không tìm thấy dự án '{normalizedProjectId}'.");
        }

        if (request.WorkflowMode == WorkflowModes.ReferenceVideo &&
            !ReferenceVideoMedia.IsOutputDurationSupported(request.TargetDurationPerSceneSeconds))
            throw new ProjectWorkflowValidationException("Thời lượng video theo mẫu phải là 10, 15, 20 hoặc 30 giây.");

        // Kiểm tra quyền sở hữu các asset được gán
        await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.ModelAssetId, "ảnh mẫu", cancellationToken);
        await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.ProductAssetId, "ảnh sản phẩm", cancellationToken);
        await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.ReadyImageAssetId, "ảnh có sẵn", cancellationToken);
        await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.ReferenceVideoAssetId, "video mẫu", cancellationToken);
        if (request.ReferenceVideoAssetId is not null &&
            !await dbContext.Assets.AnyAsync(a => a.Id == request.ReferenceVideoAssetId && a.AssetType == "Video" && a.Status == "Linked", cancellationToken))
            throw new ProjectWorkflowValidationException("Tài nguyên video mẫu phải là video đang được liên kết.");
        if (request.AudioOption == AudioOptions.CustomAudio)
        {
            await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.AudioAssetId, "tệp âm thanh", cancellationToken);
        }

        var guidanceError = PromptGuidance.Validate(request.ScriptGuidance, "Prompt kịch bản")
            ?? PromptGuidance.Validate(request.VideoGuidance, "Prompt video");
        if (guidanceError is not null) throw new ProjectWorkflowValidationException(guidanceError);

        // Lấy config hiện tại để bảo toàn foundation image và runState nếu có; trường null nghĩa là giữ nguyên
        var existing = await GetWorkflowConfigAsync(normalizedProjectId, cancellationToken);
        var videoStrategy = request.VideoStrategy ?? existing.VideoStrategy;
        if (!VideoStrategies.IsValid(videoStrategy))
        {
            throw new ProjectWorkflowValidationException($"Chiến lược video '{videoStrategy}' không hợp lệ.");
        }
        var referenceImageMode = request.ReferenceImageMode ?? existing.ReferenceImageMode;
        if (!ReferenceImageModes.IsValid(referenceImageMode))
        {
            throw new ProjectWorkflowValidationException($"Chế độ ảnh tham chiếu '{referenceImageMode}' không hợp lệ.");
        }
        // Storyboard 9 ô là một ảnh cho một video: chỉ hợp lệ với luồng một video (cấu hình thiếu trường này là Direct).
        if (referenceImageMode == ReferenceImageModes.StoryboardGrid9 && videoStrategy != VideoStrategies.SingleVideo)
        {
            throw new ProjectWorkflowValidationException(
                $"Ảnh storyboard {ReferenceImageModes.GridShotCount} ô chỉ dùng cho chế độ một video.");
        }
        var scriptGuidance = request.ScriptGuidance is null ? existing.ScriptGuidance : PromptGuidance.Normalize(request.ScriptGuidance);
        var videoGuidance = request.VideoGuidance is null ? existing.VideoGuidance : PromptGuidance.Normalize(request.VideoGuidance);
        bool workflowInputsChanged = existing.WorkflowMode != request.WorkflowMode ||
                                     existing.VideoStrategy != videoStrategy ||
                                     existing.ReferenceImageMode != referenceImageMode ||
                                     existing.GoalPrompt != (request.GoalPrompt ?? string.Empty) ||
                                     existing.ModelAssetId != request.ModelAssetId ||
                                     existing.ProductAssetId != request.ProductAssetId ||
                                     existing.ReadyImageAssetId != request.ReadyImageAssetId ||
                                     existing.ReferenceVideoAssetId != request.ReferenceVideoAssetId ||
                                     existing.AudioOption != (request.AudioOption ?? AudioOptions.None) ||
                                     existing.AudioAssetId != (request.AudioOption == AudioOptions.CustomAudio ? request.AudioAssetId : null) ||
                                     (request.TargetDurationPerSceneSeconds > 0 && existing.TargetDurationPerSceneSeconds != request.TargetDurationPerSceneSeconds) ||
                                     existing.ScriptGuidance != scriptGuidance ||
                                     existing.VideoGuidance != videoGuidance;
        if (workflowInputsChanged && existing.RunState is WorkflowRunState.Running or WorkflowRunState.Paused)
        {
            throw new ProjectWorkflowValidationException(
                "Đầu vào của run đang chạy không thể thay đổi. Hãy hủy run rồi bắt đầu run mới để giữ nguyên snapshot.");
        }

        var updated = existing with
        {
            WorkflowMode = request.WorkflowMode,
            VideoStrategy = videoStrategy,
            ReferenceImageMode = referenceImageMode,
            GoalPrompt = request.GoalPrompt ?? string.Empty,
            ModelAssetId = request.ModelAssetId,
            ProductAssetId = request.ProductAssetId,
            ReadyImageAssetId = request.ReadyImageAssetId,
            ReferenceVideoAssetId = request.ReferenceVideoAssetId,
            AudioOption = request.AudioOption ?? AudioOptions.None,
            AudioAssetId = request.AudioOption == AudioOptions.CustomAudio ? request.AudioAssetId : null,
            TargetSceneCount = request.TargetSceneCount > 0 ? request.TargetSceneCount : (existing.TargetSceneCount > 0 ? existing.TargetSceneCount : 1),
            TargetDurationPerSceneSeconds = request.TargetDurationPerSceneSeconds > 0 ? request.TargetDurationPerSceneSeconds : (existing.TargetDurationPerSceneSeconds > 0 ? existing.TargetDurationPerSceneSeconds : 15),
            ScriptGuidance = scriptGuidance,
            VideoGuidance = videoGuidance,
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
        };

        var key = GetConfigKey(normalizedProjectId);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        bool isInitialConfig = setting == null || string.IsNullOrWhiteSpace(setting.Value);
        var jsonValue = SerializeStoredConfig(updated);

        if (setting == null)
        {
            dbContext.AppSettings.Add(new AppSetting
            {
                Key = key,
                Value = jsonValue,
                UpdatedAtUtc = updated.UpdatedAtUtc
            });
        }
        else
        {
            setting.Value = jsonValue;
            setting.UpdatedAtUtc = updated.UpdatedAtUtc;
        }

        // Áp dụng ma trận phụ thuộc §3.4: Khi đổi input chung (mẫu/sản phẩm/duration/audio), đánh dấu stale các cảnh phụ thuộc
        // Chỉ áp dụng khi không phải là lần đầu tạo cấu hình dự án
        bool modelOrProductChanged = existing.ModelAssetId != request.ModelAssetId ||
                                     existing.ProductAssetId != request.ProductAssetId ||
                                     existing.ReadyImageAssetId != request.ReadyImageAssetId ||
                                     existing.ReferenceVideoAssetId != request.ReferenceVideoAssetId;
        bool workflowModeChanged = existing.WorkflowMode != updated.WorkflowMode;
        bool durationChanged = request.TargetDurationPerSceneSeconds > 0 && existing.TargetDurationPerSceneSeconds != request.TargetDurationPerSceneSeconds;
        bool audioChanged = existing.AudioOption != request.AudioOption || existing.AudioAssetId != request.AudioAssetId;
        bool briefChanged = existing.GoalPrompt != updated.GoalPrompt;
        bool strategyChanged = existing.VideoStrategy != videoStrategy || existing.ReferenceImageMode != referenceImageMode;
        bool guidanceChanged = existing.ScriptGuidance != scriptGuidance || existing.VideoGuidance != videoGuidance;

        if (!isInitialConfig && (modelOrProductChanged || workflowModeChanged || durationChanged || audioChanged || briefChanged || strategyChanged || guidanceChanged))
        {
            var scenes = await dbContext.Scenes
                .Where(s => s.ProjectId == normalizedProjectId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var sc in scenes)
            {
                sc.UpdatedAtUtc = updated.UpdatedAtUtc;
                // Cảnh đang kế thừa thời lượng cũ của dự án (hoặc chưa đặt) thì theo thời lượng mới; render ưu tiên giá trị của cảnh.
                if (durationChanged && (sc.TargetDurationSeconds <= 0 || sc.TargetDurationSeconds == existing.TargetDurationPerSceneSeconds))
                {
                    sc.TargetDurationSeconds = updated.TargetDurationPerSceneSeconds;
                }
                var sKey = GetSceneKey(sc.Id);
                var sSetting = await dbContext.AppSettings.FirstOrDefaultAsync(item => item.Key == sKey, cancellationToken).ConfigureAwait(false);
                SceneWorkflowDetailDto? existingDetail = null;
                if (sSetting != null && !string.IsNullOrWhiteSpace(sSetting.Value))
                {
                    try { existingDetail = JsonSerializer.Deserialize<SceneWorkflowDetailDto>(sSetting.Value, JsonOptions); } catch { }
                }
                var candidateHistory = existingDetail?.VideoCandidateHistory;
                var staleTasks = new HashSet<string>(existingDetail?.StaleTaskTypes ?? Array.Empty<string>(), StringComparer.Ordinal);
                bool independentImageOverride = existingDetail?.ImageStrategy == SceneImageStrategy.DirectInput;
                bool commonImageChanged = modelOrProductChanged || workflowModeChanged;
                if (commonImageChanged && !independentImageOverride)
                {
                    var oldCommonImage = VideoWorkflows.For(existing.WorkflowMode).PlanImages(existing).CommonImageAssetId;
                    var updatedImages = VideoWorkflows.For(updated.WorkflowMode).PlanImages(updated);
                    var newCommonImage = updatedImages.CommonImageAssetId;

                    // Chế độ 9 ô: ảnh của cảnh là ảnh storyboard sinh từ nguồn cũ nên phải đặt lại để sinh lại từ nguồn mới.
                    if (sc.ImageAssetId == oldCommonImage || string.IsNullOrWhiteSpace(sc.ImageAssetId) ||
                        updated.ReferenceImageMode == ReferenceImageModes.StoryboardGrid9)
                    {
                        sc.ImageAssetId = newCommonImage;
                        sc.UpdatedAtUtc = updated.UpdatedAtUtc;
                    }

                    if (updatedImages.GenerationPromptPurpose != null || updated.ReferenceImageMode == ReferenceImageModes.StoryboardGrid9)
                    {
                        staleTasks.Add(JobTaskType.Image);
                    }
                }
                if (commonImageChanged || durationChanged || audioChanged || briefChanged || strategyChanged || guidanceChanged)
                {
                    if (VideoWorkflows.For(updated.WorkflowMode).PlanImages(updated).RequiresPromptTemplates)
                        staleTasks.Add(JobTaskType.VideoPrompt);
                    else
                        sc.PromptText = null;
                    staleTasks.Add(JobTaskType.VideoRender);
                }

                string reason = commonImageChanged ? "Ảnh hoặc video đầu vào dùng cho cảnh đã thay đổi." :
                                durationChanged ? "Thời lượng clip đã thay đổi." :
                                audioChanged ? "Âm thanh dùng cho clip đã thay đổi." :
                                strategyChanged ? "Chiến lược video đã thay đổi." :
                                guidanceChanged ? "Prompt riêng của dự án đã thay đổi." :
                                "Yêu cầu nội dung chung đã thay đổi.";

                var metaObj = new SceneWorkflowDetailDto(
                    SceneId: sc.Id,
                    ProjectId: sc.ProjectId,
                    SequenceOrder: sc.SequenceOrder,
                    Name: sc.Name,
                    TargetDurationSeconds: Convert.ToInt32(sc.TargetDurationSeconds),
                    WorkflowStatus: SceneWorkflowStatus.NeedsReview,
                    ImageStrategy: existingDetail?.ImageStrategy ?? SceneImageStrategy.ReuseFoundation,
                    SourceImageAssetId: sc.ImageAssetId,
                    SelectedImageAssetId: sc.ImageAssetId,
                    MotionPromptText: sc.PromptText,
                    AudioOption: sc.AudioAssetId != null ? AudioOptions.CustomAudio : AudioOptions.None,
                    AudioAssetId: sc.AudioAssetId,
                    VideoAssetId: sc.VideoAssetId,
                    IsStale: true,
                    StaleReason: reason,
                    ActiveJobId: null,
                    ActiveJobType: null,
                    ActiveJobState: null,
                    ActiveJobProgressPercent: null,
                    VideoCandidateHistory: candidateHistory,
                    Revision: updated.UpdatedAtUtc,
                    StaleTaskTypes: staleTasks.ToArray()
                );
                var sJson = JsonSerializer.Serialize(metaObj, JsonOptions);
                if (sSetting == null)
                {
                    dbContext.AppSettings.Add(new AppSetting { Key = sKey, Value = sJson, UpdatedAtUtc = updated.UpdatedAtUtc });
                }
                else
                {
                    sSetting.Value = sJson;
                    sSetting.UpdatedAtUtc = updated.UpdatedAtUtc;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var availability = await ReferenceAvailabilityAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated with { ReferenceVideoAvailable = availability.Available, ReferenceVideoUnavailableReason = availability.Reason };
    }
}
