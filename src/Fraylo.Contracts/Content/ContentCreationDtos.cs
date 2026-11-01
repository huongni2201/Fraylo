using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Content;

/// <summary>
/// Các hằng số định danh operation tạo nội dung qua AI Web (ChatGPT/Gemini Web).
/// </summary>
public static class ContentOperations
{
    public const string SuggestIdeas = "SuggestIdeas";
    public const string GenerateScript = "GenerateScript";
    public const string GenerateVideoPrompts = "GenerateVideoPrompts";
}

/// <summary>
/// Bài đăng TikTok duy nhất cấp Project theo SOURCE §7.4.
/// </summary>
public sealed record TikTokPostDto(
    [property: JsonPropertyName("caption")] string Caption,
    [property: JsonPropertyName("hashtags")] IReadOnlyList<string> Hashtags);

/// <summary>
/// Chi tiết một ý tưởng video do AI đề xuất từ phân tích kênh (P1 / SuggestIdeas).
/// </summary>
public sealed record IdeaSuggestionDto(
    [property: JsonPropertyName("ideaId")] string IdeaId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("hook")] string Hook,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("suggestedDurationSeconds")] int SuggestedDurationSeconds,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("evidenceRefs")] IReadOnlyList<string> EvidenceRefs,
    [property: JsonPropertyName("hypothesis")] string Hypothesis,
    [property: JsonPropertyName("tiktokPost")] TikTokPostDto TikTokPost);

/// <summary>
/// Phân tích dữ liệu kênh đi kèm danh sách ý tưởng đề xuất.
/// </summary>
public sealed record ChannelAnalysisDto(
    [property: JsonPropertyName("observations")] IReadOnlyList<string> Observations,
    [property: JsonPropertyName("limitations")] IReadOnlyList<string> Limitations,
    [property: JsonPropertyName("analyzedVideoCount")] int AnalyzedVideoCount,
    [property: JsonPropertyName("periodDescription")] string? PeriodDescription);

/// <summary>
/// Kết quả candidate cho operation SuggestIdeas.
/// </summary>
public sealed record SuggestIdeasOutputDto(
    [property: JsonPropertyName("analysis")] ChannelAnalysisDto Analysis,
    [property: JsonPropertyName("ideas")] IReadOnlyList<IdeaSuggestionDto> Ideas);

/// <summary>
/// Cảnh quay trong kịch bản kịch bản được tạo bởi AI.
/// </summary>
public sealed record ScriptSceneDto(
    [property: JsonPropertyName("sceneKey")] string SceneKey,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("targetDurationSeconds")] int TargetDurationSeconds,
    [property: JsonPropertyName("imagePrompt")] string ImagePrompt);

/// <summary>
/// Kết quả candidate cho operation GenerateScript.
/// </summary>
public sealed record GenerateScriptOutputDto(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("concept")] string Concept,
    [property: JsonPropertyName("assumptions")] IReadOnlyList<string> Assumptions,
    [property: JsonPropertyName("continuityNotes")] string? ContinuityNotes,
    [property: JsonPropertyName("scenes")] IReadOnlyList<ScriptSceneDto> Scenes,
    [property: JsonPropertyName("tiktokPost")] TikTokPostDto TikTokPost);

/// <summary>
/// Prompt video tiếng Anh cho từng cảnh cụ thể.
/// </summary>
public sealed record VideoPromptSceneDto(
    [property: JsonPropertyName("sceneKey")] string SceneKey,
    [property: JsonPropertyName("promptEn")] string PromptEn,
    [property: JsonPropertyName("continuityNotes")] string? ContinuityNotes);

/// <summary>
/// Kết quả candidate cho operation GenerateVideoPrompts.
/// </summary>
public sealed record GenerateVideoPromptsOutputDto(
    [property: JsonPropertyName("scenes")] IReadOnlyList<VideoPromptSceneDto> Scenes,
    [property: JsonPropertyName("tiktokPost")] TikTokPostDto TikTokPost);

/// <summary>
/// Snapshot dữ liệu Analytics bất biến dùng làm ngữ cảnh cho AI phân tích.
/// Lưu ý: Giá trị số liệu null biểu thị thiếu dữ liệu, phân biệt rạch ròi với 0 thật.
/// </summary>
public sealed record AnalyticsContextDto(
    [property: JsonPropertyName("snapshotId")] string SnapshotId,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("channelIdentity")] string ChannelIdentity,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("capturedAtUtc")] string CapturedAtUtc,
    [property: JsonPropertyName("periodStartUtc")] string PeriodStartUtc,
    [property: JsonPropertyName("periodEndUtc")] string PeriodEndUtc,
    [property: JsonPropertyName("timezone")] string Timezone,
    [property: JsonPropertyName("isComplete")] bool IsComplete,
    [property: JsonPropertyName("metrics")] IReadOnlyDictionary<string, double?> Metrics,
    [property: JsonPropertyName("videoRecordIds")] IReadOnlyList<string> VideoRecordIds,
    [property: JsonPropertyName("selectionManifestJson")] string? SelectionManifestJson);

/// <summary>
/// Yêu cầu gửi tới bộ tạo nội dung Fraylo.
/// </summary>
public sealed record ContentRequestDto(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sceneIds")] IReadOnlyList<string>? SceneIds,
    [property: JsonPropertyName("expectedProjectRevision")] int? ExpectedProjectRevision,
    [property: JsonPropertyName("expectedSceneRevisions")] IReadOnlyDictionary<string, int>? ExpectedSceneRevisions,
    [property: JsonPropertyName("selectedIdeaId")] string? SelectedIdeaId,
    [property: JsonPropertyName("channelContext")] string? ChannelContext,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, string>? Options,
    [property: JsonPropertyName("clientRequestId")] string ClientRequestId);

/// <summary>
/// Snapshot bất biến của job tạo nội dung được lưu trong DB trước khi enqueue.
/// </summary>
public sealed record ContentJobSnapshotDto(
    [property: JsonPropertyName("snapshotId")] string SnapshotId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("analyticsSnapshotHash")] string? AnalyticsSnapshotHash,
    [property: JsonPropertyName("renderedPromptText")] string RenderedPromptText,
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("targetProvider")] string TargetProvider,
    [property: JsonPropertyName("capturedAtUtc")] string CapturedAtUtc);

/// <summary>
/// Kết quả phân tích cú pháp Candidate thô từ phản hồi của AI Web.
/// </summary>
public sealed record ContentCandidateResult<T>(
    [property: JsonPropertyName("rawText")] string RawText,
    [property: JsonPropertyName("candidate")] T? Candidate,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("isCompliant")] bool IsCompliant);
