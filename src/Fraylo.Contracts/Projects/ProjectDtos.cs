using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Projects;

/// <summary>
/// Bản ghi tóm tắt Dự án phục vụ danh sách hiển thị.
/// </summary>
public sealed record ProjectSummaryDto(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("sceneCount")] int SceneCount,
    [property: JsonPropertyName("hasVideo")] bool HasVideo,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc,
    [property: JsonPropertyName("groupId")] string? GroupId = null,
    [property: JsonPropertyName("groupName")] string? GroupName = null,
    [property: JsonPropertyName("previewVideoAssetId")] string? PreviewVideoAssetId = null);

/// <summary>
/// Bản ghi chi tiết Dự án.
/// </summary>
public sealed record ProjectDetailDto(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc,
    [property: JsonPropertyName("groupId")] string? GroupId = null,
    [property: JsonPropertyName("groupName")] string? GroupName = null);

/// <summary>
/// Yêu cầu tạo mới Dự án.
/// </summary>
public sealed record ProjectCreateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("groupId")] string? GroupId = null);

public sealed record ProjectCreateResponseData(
    [property: JsonPropertyName("projectId")] string ProjectId);

/// <summary>
/// Yêu cầu cập nhật Dự án. groupId: null giữ nguyên, chuỗi rỗng bỏ khỏi nhóm.
/// </summary>
public sealed record ProjectUpdateRequest(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("groupId")] string? GroupId = null);

public sealed record ProjectUpdateResponseData(
    [property: JsonPropertyName("project")] ProjectDetailDto Project);

/// <summary>
/// Yêu cầu xóa Dự án.
/// </summary>
public sealed record ProjectDeleteRequest(
    [property: JsonPropertyName("projectId")] string ProjectId);

public sealed record ProjectDeleteResponseData(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("success")] bool Success);

public sealed record ProjectListResponseData(
    [property: JsonPropertyName("projects")] IReadOnlyList<ProjectSummaryDto> Projects);

/// <summary>
/// Yêu cầu lấy chi tiết Dự án.
/// </summary>
public sealed record ProjectGetRequest(
    [property: JsonPropertyName("projectId")] string ProjectId);

public sealed record ProjectGetResponseData(
    [property: JsonPropertyName("project")] ProjectDetailDto Project);
