using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TiToAI.Application.Prompts;
using TiToAI.Contracts.Content;
using TiToAI.Contracts.Jobs;
using TiToAI.Contracts.Scenes;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Application.Content;

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
    /// Lấy cấu hình quy trình sản xuất video cấp Dự án.
    /// Nếu chưa có, trả về giá trị mặc định hợp lệ.
    /// </summary>
    public async Task<ProjectWorkflowConfigDto> GetWorkflowConfigAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));

        await using var dbContext = new TiToAIDbContext(_dbOptions);
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

        // Tên và tệp mặc định của nhóm: hướng dẫn prompt được ghép khi tạo prompt (VideoWorkflowPromptComposer).
        var group = project.GroupId is null ? null : await dbContext.ProjectGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == project.GroupId, cancellationToken);

        var stored = await LoadStoredConfigAsync(dbContext, normalizedProjectId, cancellationToken);
        return ApplyGroupDefaults(stored, group) with
        {
            FinalVideoAssetId = finalVideoAssetId,
            GroupId = project.GroupId,
            GroupName = group?.Name
        };
    }

    /// <summary>
    /// Cấu hình đã lưu của Dự án, chưa điền tệp kế thừa từ Nhóm. Chưa có thì trả về mặc định.
    /// </summary>
    private static async Task<ProjectWorkflowConfigDto> LoadStoredConfigAsync(
        TiToAIDbContext dbContext,
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
                    return stored;
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
    /// Điền tệp của Nhóm vào các ô tệp Dự án để trống. Âm thanh của Nhóm chỉ áp dụng khi Dự án dùng âm thanh tùy chọn.
    /// Chỉ thay đổi kết quả đọc; cấu hình lưu không chứa tệp kế thừa (xem ToStoredConfig).
    /// </summary>
    private static ProjectWorkflowConfigDto ApplyGroupDefaults(ProjectWorkflowConfigDto config, ProjectGroup? group)
    {
        var inherited = new List<string>();
        string? Inherit(string slot, string? own, string? groupDefault)
        {
            if (own is not null) return own;
            if (groupDefault is null) return null;
            inherited.Add(slot);
            return groupDefault;
        }

        var modelId = Inherit("Model", config.ModelAssetId, group?.ModelAssetId);
        var productId = Inherit("Product", config.ProductAssetId, group?.ProductAssetId);
        var readyId = Inherit("ReadyImage", config.ReadyImageAssetId, group?.ReadyImageAssetId);
        var audioId = Inherit("Audio", config.AudioAssetId, config.AudioOption == AudioOptions.CustomAudio ? group?.AudioAssetId : null);

        return config with
        {
            ModelAssetId = modelId,
            ProductAssetId = productId,
            ReadyImageAssetId = readyId,
            AudioAssetId = audioId,
            InheritedAssetSlots = inherited.Count > 0 ? inherited : null
        };
    }

    /// <summary>
    /// Đưa cấu hình về dạng lưu. Tệp trùng mặc định của Nhóm được bỏ để Dự án tiếp tục theo Nhóm;
    /// các trường tính toán (video cuối, nhóm, tệp kế thừa) không được ghi.
    /// </summary>
    private static ProjectWorkflowConfigDto ToStoredConfig(ProjectWorkflowConfigDto config, ProjectGroup? group)
    {
        static string? OwnOnly(string? own, string? groupDefault) => own is not null && own == groupDefault ? null : own;

        return config with
        {
            ModelAssetId = OwnOnly(config.ModelAssetId, group?.ModelAssetId),
            ProductAssetId = OwnOnly(config.ProductAssetId, group?.ProductAssetId),
            ReadyImageAssetId = OwnOnly(config.ReadyImageAssetId, group?.ReadyImageAssetId),
            AudioAssetId = OwnOnly(config.AudioAssetId, config.AudioOption == AudioOptions.CustomAudio ? group?.AudioAssetId : null),
            FinalVideoAssetId = null,
            GroupId = null,
            GroupName = null,
            InheritedAssetSlots = null
        };
    }

    /// <summary>
    /// Ghi cấu hình vào AppSettings theo dạng lưu. Dùng cho mọi nơi ghi lại cấu hình sau khi đã đọc hiệu lực.
    /// </summary>
    private static async Task<string> SerializeStoredConfigAsync(
        TiToAIDbContext dbContext,
        ProjectWorkflowConfigDto config,
        CancellationToken cancellationToken)
    {
        var group = await FindProjectGroupAsync(dbContext, config.ProjectId, cancellationToken);
        return JsonSerializer.Serialize(ToStoredConfig(config, group), JsonOptions);
    }

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

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var project = await dbContext.Projects
            .FirstOrDefaultAsync(p => p.Id == normalizedProjectId, cancellationToken);

        if (project == null)
        {
            throw new ProjectWorkflowValidationException($"Không tìm thấy dự án '{normalizedProjectId}'.");
        }

        // Kiểm tra quyền sở hữu các asset được gán
        await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.ModelAssetId, "ảnh mẫu", cancellationToken);
        await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.ProductAssetId, "ảnh sản phẩm", cancellationToken);
        await ValidateAssetBelongsToProjectAsync(dbContext, normalizedProjectId, request.ReadyImageAssetId, "ảnh có sẵn", cancellationToken);
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
        // Storyboard 9 ô là một ảnh cho một video 15 giây: chỉ hợp lệ với luồng một video (cấu hình thiếu trường này là Direct).
        if (referenceImageMode == ReferenceImageModes.StoryboardGrid9)
        {
            var requestedDuration = request.TargetDurationPerSceneSeconds > 0 ? request.TargetDurationPerSceneSeconds : existing.TargetDurationPerSceneSeconds;
            if (videoStrategy != VideoStrategies.SingleVideo || requestedDuration != ReferenceImageModes.GridDurationSeconds)
            {
                throw new ProjectWorkflowValidationException(
                    $"Ảnh storyboard {ReferenceImageModes.GridShotCount} ô chỉ dùng cho chế độ một video, thời lượng {ReferenceImageModes.GridDurationSeconds} giây.");
            }
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
        var group = await FindProjectGroupAsync(dbContext, normalizedProjectId, cancellationToken);
        var effectiveUpdated = ApplyGroupDefaults(updated, group);
        var jsonValue = JsonSerializer.Serialize(ToStoredConfig(updated, group), JsonOptions);

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
                                     existing.ReadyImageAssetId != request.ReadyImageAssetId;
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
                    string? oldCommonImage = existing.WorkflowMode == WorkflowModes.ReadyImage
                        ? existing.ReadyImageAssetId
                        : existing.WorkflowMode == WorkflowModes.ProductOnly ? existing.ProductAssetId : null;
                    string? newCommonImage = effectiveUpdated.WorkflowMode == WorkflowModes.ReadyImage
                        ? effectiveUpdated.ReadyImageAssetId
                        : effectiveUpdated.WorkflowMode == WorkflowModes.ProductOnly ? effectiveUpdated.ProductAssetId : null;

                    // Chế độ 9 ô: ảnh của cảnh là ảnh storyboard sinh từ nguồn cũ nên phải đặt lại để sinh lại từ nguồn mới.
                    if (sc.ImageAssetId == oldCommonImage || string.IsNullOrWhiteSpace(sc.ImageAssetId) ||
                        updated.ReferenceImageMode == ReferenceImageModes.StoryboardGrid9)
                    {
                        sc.ImageAssetId = newCommonImage;
                        sc.UpdatedAtUtc = updated.UpdatedAtUtc;
                    }

                    if (CanGenerateImage(updated.WorkflowMode, videoStrategy) || updated.ReferenceImageMode == ReferenceImageModes.StoryboardGrid9)
                    {
                        staleTasks.Add(JobTaskType.Image);
                    }
                }
                if (commonImageChanged || durationChanged || audioChanged || briefChanged || strategyChanged || guidanceChanged)
                {
                    staleTasks.Add(JobTaskType.VideoPrompt);
                    staleTasks.Add(JobTaskType.VideoRender);
                }

                string reason = commonImageChanged ? "Ảnh mẫu/sản phẩm dùng cho cảnh đã thay đổi." :
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
        return ApplyGroupDefaults(updated, group);
    }

    /// <summary>
    /// Cảnh cần job Image riêng: Mẫu + sản phẩm (cả hai luồng); Chỉ sản phẩm chỉ khi nhiều video (D1 của VIDEO_TWO_FLOW_SPLIT).
    /// </summary>
    private static bool CanGenerateImage(string workflowMode, string videoStrategy) =>
        workflowMode == WorkflowModes.ModelAndProduct ||
        (workflowMode == WorkflowModes.ProductOnly && videoStrategy == VideoStrategies.MultiVideo);
}
