using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Scenes;

/// <summary>
/// DTO đại diện cho một Cảnh (Scene) trong Dự án.
/// </summary>
public sealed record SceneDto(
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sceneSequence")] int SceneSequence,
    [property: JsonPropertyName("targetDurationSeconds")] int TargetDurationSeconds,
    [property: JsonPropertyName("promptText")] string? PromptText,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId,
    [property: JsonPropertyName("imageAssetId")] string? ImageAssetId,
    [property: JsonPropertyName("videoAssetId")] string? VideoAssetId,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc,
    [property: JsonPropertyName("revision")] string Revision);

/// <summary>
/// Yêu cầu lấy danh sách Cảnh theo Dự án.
/// </summary>
public sealed record SceneListByProjectRequest(
    [property: JsonPropertyName("projectId")] string ProjectId);

public sealed record SceneListByProjectResponseData(
    [property: JsonPropertyName("scenes")] IReadOnlyList<SceneDto> Scenes);

/// <summary>
/// Yêu cầu lấy chi tiết một Cảnh.
/// </summary>
public sealed record SceneGetRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sceneId")] string SceneId);

public sealed record SceneGetResponseData(
    [property: JsonPropertyName("scene")] SceneDto Scene);

/// <summary>
/// Yêu cầu tạo mới một Cảnh.
/// </summary>
public sealed record SceneCreateRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("promptText")] string? PromptText,
    [property: JsonPropertyName("targetDurationSeconds")] int? TargetDurationSeconds,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId,
    [property: JsonPropertyName("imageAssetId")] string? ImageAssetId,
    [property: JsonPropertyName("videoAssetId")] string? VideoAssetId);

public sealed record SceneCreateResponseData(
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("sceneSequence")] int SceneSequence);

/// <summary>
/// Yêu cầu cập nhật một Cảnh với expectedRevision phục vụ kiểm tra xung đột đồng thời (optimistic concurrency).
/// </summary>
public sealed record SceneUpdateRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("expectedRevision")] string? ExpectedRevision = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("promptText")] string? PromptText = null,
    [property: JsonPropertyName("targetDurationSeconds")] int? TargetDurationSeconds = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("audioAssetId")] string? AudioAssetId = null,
    [property: JsonPropertyName("imageAssetId")] string? ImageAssetId = null,
    [property: JsonPropertyName("videoAssetId")] string? VideoAssetId = null);

public sealed record SceneUpdateResponseData(
    [property: JsonPropertyName("scene")] SceneDto Scene);

/// <summary>
/// Yêu cầu xóa một Cảnh.
/// </summary>
public sealed record SceneDeleteRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sceneId")] string SceneId);

public sealed record SceneDeleteResponseData(
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("success")] bool Success);

/// <summary>
/// Yêu cầu sắp xếp lại thứ tự các Cảnh trong Dự án (Reorder), hỗ trợ expectedProjectRevision và expectedSceneRevisions.
/// </summary>
public sealed record SceneReorderRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sceneIds")] IReadOnlyList<string> SceneIds,
    [property: JsonPropertyName("expectedProjectRevision")] string? ExpectedProjectRevision = null,
    [property: JsonPropertyName("expectedSceneRevisions")] IReadOnlyDictionary<string, string>? ExpectedSceneRevisions = null);

public sealed record SceneReorderResponseData(
    [property: JsonPropertyName("reordered")] bool Reordered,
    [property: JsonPropertyName("scenes")] IReadOnlyList<SceneDto> Scenes);
