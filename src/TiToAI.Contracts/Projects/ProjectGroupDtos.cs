using System.Text.Json.Serialization;

namespace TiToAI.Contracts.Projects;

/// <summary>
/// Nhóm dự án: hướng dẫn prompt và biến dùng chung cho các Dự án trong nhóm.
/// </summary>
public sealed record ProjectGroupDto(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("scriptGuidance")] string ScriptGuidance,
    [property: JsonPropertyName("videoGuidance")] string VideoGuidance,
    [property: JsonPropertyName("promptVariables")] IReadOnlyDictionary<string, string> PromptVariables,
    [property: JsonPropertyName("projectCount")] int ProjectCount,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc,
    [property: JsonPropertyName("modelAssetId")] string? ModelAssetId = null,
    [property: JsonPropertyName("productAssetId")] string? ProductAssetId = null,
    [property: JsonPropertyName("readyImageAssetId")] string? ReadyImageAssetId = null,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId = null);

public sealed record ProjectGroupListResponseData(
    [property: JsonPropertyName("groups")] IReadOnlyList<ProjectGroupDto> Groups);

/// <summary>
/// Yêu cầu tạo mới hoặc cập nhật Nhóm (groupId rỗng là tạo mới).
/// </summary>
public sealed record ProjectGroupSaveRequest(
    [property: JsonPropertyName("groupId")] string? GroupId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("scriptGuidance")] string? ScriptGuidance,
    [property: JsonPropertyName("videoGuidance")] string? VideoGuidance,
    [property: JsonPropertyName("promptVariables")] IReadOnlyDictionary<string, string>? PromptVariables,
    [property: JsonPropertyName("modelAssetId")] string? ModelAssetId = null,
    [property: JsonPropertyName("productAssetId")] string? ProductAssetId = null,
    [property: JsonPropertyName("readyImageAssetId")] string? ReadyImageAssetId = null,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId = null);

public sealed record ProjectGroupSaveResponseData(
    [property: JsonPropertyName("group")] ProjectGroupDto Group);

public sealed record ProjectGroupDeleteRequest(
    [property: JsonPropertyName("groupId")] string GroupId);

public sealed record ProjectGroupDeleteResponseData(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("success")] bool Success);
