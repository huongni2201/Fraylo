using System.Text.Json.Serialization;

namespace TiToAI.Contracts.Content;

/// <summary>
/// Ba chế độ bắt đầu của quy trình sản xuất video từ ảnh theo PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN §2.
/// </summary>
public static class WorkflowModes
{
    /// <summary>
    /// Mẫu + sản phẩm: Cần cả ảnh mẫu và ảnh sản phẩm; AI tạo ảnh mẫu mặc/dùng sản phẩm.
    /// </summary>
    public const string ModelAndProduct = "ModelAndProduct";

    /// <summary>
    /// Ảnh đã sẵn sàng: Ảnh mẫu đã mặc sẵn sản phẩm; đi thẳng sang viết prompt video.
    /// </summary>
    public const string ReadyImage = "ReadyImage";

    /// <summary>
    /// Chỉ sản phẩm: Chỉ cần ảnh sản phẩm, không bắt ảnh mẫu; dùng trực tiếp hoặc AI đổi nền.
    /// </summary>
    public const string ProductOnly = "ProductOnly";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ModelAndProduct,
        ReadyImage,
        ProductOnly
    };

    public static bool IsValid(string? mode) =>
        !string.IsNullOrWhiteSpace(mode) && All.Contains(mode);
}

/// <summary>
/// Chiến lược video của một run (VIDEO_TWO_FLOW_SPLIT §3.1): nhiều video hoặc một video nhiều phân cảnh.
/// </summary>
public static class VideoStrategies
{
    /// <summary>Luồng A: mỗi phân cảnh là một clip riêng, có ảnh và prompt riêng.</summary>
    public const string MultiVideo = "MultiVideo";

    /// <summary>Luồng B: một storyboard theo mốc thời gian, một prompt và một video cho cả run.</summary>
    public const string SingleVideo = "SingleVideo";

    public static readonly IReadOnlyList<string> All = new[]
    {
        MultiVideo,
        SingleVideo
    };

    public static bool IsValid(string? strategy) =>
        !string.IsNullOrWhiteSpace(strategy) && All.Contains(strategy);
}

/// <summary>
/// Các vai trò của Asset hình ảnh trong quy trình sản xuất video từ ảnh.
/// </summary>
public static class AssetWorkflowRoles
{
    public const string ModelReference = "ModelReference";
    public const string ProductReference = "ProductReference";
    public const string ReadyImage = "ReadyImage";
    public const string FoundationImage = "FoundationImage";
    public const string SceneVariantImage = "SceneVariantImage";
    public const string SelectedImage = "SelectedImage";
}

/// <summary>
/// Tùy chọn âm thanh theo quyết định đã chốt: Audio không bắt buộc.
/// </summary>
public static class AudioOptions
{
    public const string None = "None";
    public const string CustomAudio = "CustomAudio";
}

/// <summary>
/// Trạng thái tiến trình của quy trình sản xuất video (Pipeline Run State).
/// </summary>
public static class WorkflowRunState
{
    public const string Idle = "Idle";
    public const string Preparing = "Preparing";
    public const string Running = "Running";
    public const string Paused = "Paused";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>
/// Trạng thái sẵn sàng và tiến độ của từng Cảnh (Scene Workflow Status).
/// Tách biệt rõ ràng với Job State kỹ thuật (queued/reserved/running/succeeded/failed).
/// </summary>
public static class SceneWorkflowStatus
{
    public const string MissingInputs = "MissingInputs";
    public const string PreparingImage = "PreparingImage";
    public const string PreparingPrompt = "PreparingPrompt";
    public const string ReadyForVideo = "ReadyForVideo";
    public const string VideoCompleted = "VideoCompleted";
    public const string NeedsReview = "NeedsReview";
}

/// <summary>
/// Chiến lược hình ảnh của từng cảnh: Dùng lại ảnh nền tảng, Tạo biến thể hoặc Dùng ảnh trực tiếp.
/// </summary>
public static class SceneImageStrategy
{
    public const string ReuseFoundation = "ReuseFoundation";
    public const string GenerateVariant = "GenerateVariant";
    public const string DirectInput = "DirectInput";
}

/// <summary>
/// Cấu hình quy trình sản xuất video cấp Dự án (đầu vào dùng chung kế thừa cho các cảnh).
/// </summary>
public sealed record ProjectWorkflowConfigDto(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("workflowMode")] string WorkflowMode,
    [property: JsonPropertyName("goalPrompt")] string GoalPrompt,
    [property: JsonPropertyName("modelAssetId")] string? ModelAssetId,
    [property: JsonPropertyName("productAssetId")] string? ProductAssetId,
    [property: JsonPropertyName("readyImageAssetId")] string? ReadyImageAssetId,
    [property: JsonPropertyName("foundationImageAssetId")] string? FoundationImageAssetId,
    [property: JsonPropertyName("audioOption")] string AudioOption,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId,
    [property: JsonPropertyName("targetSceneCount")] int TargetSceneCount,
    [property: JsonPropertyName("targetDurationPerSceneSeconds")] int TargetDurationPerSceneSeconds,
    [property: JsonPropertyName("runState")] string RunState,
    [property: JsonPropertyName("currentRunId")] string? CurrentRunId,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc,
    [property: JsonPropertyName("scriptGuidance")] string? ScriptGuidance = null,
    [property: JsonPropertyName("videoGuidance")] string? VideoGuidance = null,
    [property: JsonPropertyName("promptVariables")] IReadOnlyDictionary<string, string>? PromptVariables = null,
    [property: JsonPropertyName("finalVideoAssetId")] string? FinalVideoAssetId = null,
    [property: JsonPropertyName("groupId")] string? GroupId = null,
    [property: JsonPropertyName("groupName")] string? GroupName = null,
    [property: JsonPropertyName("inheritedAssetSlots")] IReadOnlyList<string>? InheritedAssetSlots = null,
    [property: JsonPropertyName("videoStrategy")] string VideoStrategy = VideoStrategies.MultiVideo);

/// <summary>
/// Thông tin chi tiết quy trình của một Cảnh (Scene Workflow Detail).
/// </summary>
public sealed record SceneWorkflowDetailDto(
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sequenceOrder")] int SequenceOrder,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("targetDurationSeconds")] int TargetDurationSeconds,
    [property: JsonPropertyName("workflowStatus")] string WorkflowStatus,
    [property: JsonPropertyName("imageStrategy")] string ImageStrategy,
    [property: JsonPropertyName("sourceImageAssetId")] string? SourceImageAssetId,
    [property: JsonPropertyName("selectedImageAssetId")] string? SelectedImageAssetId,
    [property: JsonPropertyName("motionPromptText")] string? MotionPromptText,
    [property: JsonPropertyName("audioOption")] string AudioOption,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId,
    [property: JsonPropertyName("videoAssetId")] string? VideoAssetId,
    [property: JsonPropertyName("isStale")] bool IsStale,
    [property: JsonPropertyName("staleReason")] string? StaleReason,
    [property: JsonPropertyName("activeJobId")] string? ActiveJobId,
    [property: JsonPropertyName("activeJobType")] string? ActiveJobType,
    [property: JsonPropertyName("activeJobState")] string? ActiveJobState,
    [property: JsonPropertyName("activeJobProgressPercent")] int? ActiveJobProgressPercent,
    [property: JsonPropertyName("videoCandidateHistory")] IReadOnlyList<string>? VideoCandidateHistory,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("staleTaskTypes")] IReadOnlyList<string>? StaleTaskTypes = null);

/// <summary>
/// Yêu cầu lấy cấu hình workflow của Dự án.
/// </summary>
public sealed record GetProjectWorkflowConfigRequest(
    [property: JsonPropertyName("projectId")] string ProjectId);

/// <summary>
/// Yêu cầu lưu cấu hình workflow của Dự án.
/// </summary>
public sealed record SaveProjectWorkflowConfigRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("workflowMode")] string WorkflowMode,
    [property: JsonPropertyName("goalPrompt")] string GoalPrompt,
    [property: JsonPropertyName("modelAssetId")] string? ModelAssetId,
    [property: JsonPropertyName("productAssetId")] string? ProductAssetId,
    [property: JsonPropertyName("readyImageAssetId")] string? ReadyImageAssetId,
    [property: JsonPropertyName("audioOption")] string AudioOption,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId,
    [property: JsonPropertyName("targetSceneCount")] int TargetSceneCount,
    [property: JsonPropertyName("targetDurationPerSceneSeconds")] int TargetDurationPerSceneSeconds,
    [property: JsonPropertyName("scriptGuidance")] string? ScriptGuidance = null,
    [property: JsonPropertyName("videoGuidance")] string? VideoGuidance = null,
    [property: JsonPropertyName("promptVariables")] IReadOnlyDictionary<string, string>? PromptVariables = null,
    [property: JsonPropertyName("videoStrategy")] string? VideoStrategy = null);

/// <summary>
/// Yêu cầu bấm "Bắt đầu tạo video" một lần duy nhất cho toàn bộ dự án.
/// Hỗ trợ clientRequestId để bảo đảm tính Idempotent khi bấm lặp lại.
/// </summary>
public sealed record StartProjectWorkflowRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("clientRequestId")] string? ClientRequestId = null);

public sealed record StartProjectWorkflowResponseData(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("runState")] string RunState,
    [property: JsonPropertyName("createdSceneCount")] int CreatedSceneCount,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Yêu cầu cập nhật dữ liệu đầu vào cho một Cảnh cụ thể (Override).
/// </summary>
public sealed record UpdateSceneWorkflowInputRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("imageStrategy")] string? ImageStrategy = null,
    [property: JsonPropertyName("selectedImageAssetId")] string? SelectedImageAssetId = null,
    [property: JsonPropertyName("motionPromptText")] string? MotionPromptText = null,
    [property: JsonPropertyName("audioOption")] string? AudioOption = null,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId = null,
    [property: JsonPropertyName("targetDurationSeconds")] int? TargetDurationSeconds = null,
    [property: JsonPropertyName("expectedRevision")] string? ExpectedRevision = null);

/// <summary>
/// Yêu cầu tạo lại (Re-run) một phần hoặc một cảnh cụ thể theo ma trận phụ thuộc §3.4.
/// </summary>
public sealed record RetrySceneWorkflowRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("targetStep")] string TargetStep // "Image" | "Prompt" | "Video"
);

/// <summary>
/// Mục cảnh trong kế hoạch lập cảnh tự động của AI (Scene Plan Item).
/// </summary>
public sealed record ScenePlanItemDto(
    [property: JsonPropertyName("sequenceOrder")] int SequenceOrder,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("targetDurationSeconds")] int TargetDurationSeconds,
    [property: JsonPropertyName("imageStrategy")] string ImageStrategy, // ReuseFoundation | GenerateVariant | DirectInput
    [property: JsonPropertyName("motionPromptDraft")] string MotionPromptDraft
);

/// <summary>
/// Kết quả lập danh sách cảnh tự động từ AI (AI Scene Planning Result).
/// </summary>
public sealed record AiScenePlanningResultDto(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("goalPrompt")] string GoalPrompt,
    [property: JsonPropertyName("scenes")] IReadOnlyList<ScenePlanItemDto> Scenes,
    [property: JsonPropertyName("plannedAtUtc")] string PlannedAtUtc
);

/// <summary>
/// Kết quả tạo ảnh nền tảng hoặc ảnh biến thể từ AI (Image Generation Result).
/// </summary>
public sealed record ImageGenerationResultDto(
    [property: JsonPropertyName("assetId")] string AssetId,
    [property: JsonPropertyName("role")] string Role, // FoundationImage | SceneVariantImage
    [property: JsonPropertyName("sourceAssetIds")] IReadOnlyList<string> SourceAssetIds,
    [property: JsonPropertyName("providerKey")] string ProviderKey,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc
);

/// <summary>
/// Kết quả AI viết prompt chuyển động từ ảnh (Motion Prompt Generation Result).
/// </summary>
public sealed record MotionPromptGenerationResultDto(
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("sourceImageAssetId")] string SourceImageAssetId,
    [property: JsonPropertyName("motionPromptText")] string MotionPromptText,
    [property: JsonPropertyName("providerKey")] string ProviderKey,
    [property: JsonPropertyName("generatedAtUtc")] string GeneratedAtUtc
);

/// <summary>
/// Yêu cầu xem trước prompt tổng hợp gửi AI writer theo CLIP_DURATION_STORYBOARD_PLAN §3.1–3.3.
/// </summary>
public sealed record PreviewAiPromptRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sceneId")] string? SceneId = null
);

/// <summary>
/// Dữ liệu phản hồi xem trước prompt gửi AI writer.
/// </summary>
public sealed record PreviewAiPromptResponseData(
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("templateName")] string TemplateName,
    [property: JsonPropertyName("composedPrompt")] string ComposedPrompt,
    [property: JsonPropertyName("outputContractVersion")] int OutputContractVersion
);

/// <summary>
/// DTO chứa nội dung kịch bản và prompt video được AI tạo tự động theo schema { idea, hook, video_script, video_prompt, tiktok_content }.
/// Hiển thị dạng bảng cho người dùng xem và quyết định tạo video.
/// </summary>
public sealed record VideoContentItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sceneId")] string? SceneId,
    [property: JsonPropertyName("sequenceOrder")] int SequenceOrder,
    [property: JsonPropertyName("idea")] string Idea,
    [property: JsonPropertyName("hook")] string Hook,
    [property: JsonPropertyName("video_script")] string VideoScript,
    [property: JsonPropertyName("video_prompt")] string VideoPrompt,
    [property: JsonPropertyName("tiktok_content")] string TikTokContent,
    [property: JsonPropertyName("imageAssetId")] string? ImageAssetId,
    [property: JsonPropertyName("videoAssetId")] string? VideoAssetId,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc,
    // Lấy từ Scene khi liệt kê: nội dung phân cảnh do Script AI trả về và prompt ảnh do Script AI trả về.
    [property: JsonPropertyName("contentText")] string? ContentText = null,
    [property: JsonPropertyName("imagePrompt")] string? ImagePrompt = null
);

/// <summary>
/// Yêu cầu cập nhật nội dung hàng kịch bản trong bảng.
/// </summary>
public sealed record UpdateVideoContentItemRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("idea")] string? Idea = null,
    [property: JsonPropertyName("hook")] string? Hook = null,
    [property: JsonPropertyName("video_script")] string? VideoScript = null,
    [property: JsonPropertyName("video_prompt")] string? VideoPrompt = null,
    [property: JsonPropertyName("tiktok_content")] string? TikTokContent = null
);

/// <summary>
/// Yêu cầu tạo video từ hàng kịch bản trong bảng (Action Tạo video).
/// </summary>
public sealed record CreateSceneVideoRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("contentItemId")] string? ContentItemId = null,
    [property: JsonPropertyName("sceneId")] string? SceneId = null
);

