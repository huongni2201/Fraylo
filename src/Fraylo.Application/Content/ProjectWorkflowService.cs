using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Content.Workflows;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Content;

public sealed class ProjectWorkflowValidationException : Exception
{
    public ProjectWorkflowValidationException(string message) : base(message) { }
}

/// <summary>Hủy run chưa xong: còn job chưa dừng được. Run ở trạng thái Paused để người dùng hủy lại sau khi đối soát.</summary>
public sealed class ProjectWorkflowCancelIncompleteException : Exception
{
    public IReadOnlyList<string> UnresolvedJobIds { get; }

    public ProjectWorkflowCancelIncompleteException(IReadOnlyList<string> unresolvedJobIds)
        : base($"Còn {unresolvedJobIds.Count} tác vụ chưa dừng được. Quy trình đã tạm dừng; hãy hủy lại sau khi tác vụ kết thúc hoặc đối soát.")
    {
        UnresolvedJobIds = unresolvedJobIds;
    }
}

public sealed class SceneConcurrencyConflictException : Exception
{
    public string SceneId { get; }
    public string ExpectedRevision { get; }
    public string ActualRevision { get; }

    public SceneConcurrencyConflictException(string sceneId, string expectedRevision, string actualRevision)
        : base($"Xung đột đồng thời khi cập nhật Cảnh '{sceneId}': Kỳ vọng revision '{expectedRevision}', thực tế '{actualRevision}'.")
    {
        SceneId = sceneId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }
}

/// <summary>
/// Ý định chuyển tiếp bước tiếp theo trong quy trình (Continuation Intent).
/// Được commit trong cùng một SQLite transaction với apply marker để bảo đảm phục hồi sau sự cố (Crash Recovery).
/// </summary>
public sealed record WorkflowContinuationIntent(
    string IntentId,
    string ProjectId,
    string? SceneId,
    string TaskType,
    string? CustomPromptText,
    JobMediaSnapshotDto? MediaSnapshot,
    string RunId,
    string CreatedAtUtc,
    string? WorkflowStepKey = null
);

/// <summary>
/// Bản ghi trạng thái của một lần chạy quy trình (Workflow Run Record).
/// </summary>
public sealed record WorkflowRunRecord(
    string RunId,
    string ProjectId,
    string WorkflowMode,
    string GoalPrompt,
    string? ModelAssetId,
    string? ProductAssetId,
    string? ReadyImageAssetId,
    string AudioOption,
    string? AudioAssetId,
    int TargetSceneCount,
    int TargetDurationPerSceneSeconds,
    string RunState,
    string? ClientRequestId,
    IReadOnlyList<string> SelectedSceneIds,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    string? ReferenceVideoAssetId = null,
    bool UseReferenceVideoAnalysis = false
);

/// <summary>
/// Dịch vụ quản lý quy trình sản xuất video từ ảnh theo PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN.md.
/// Điều phối 3 chế độ bắt đầu, lập cảnh có mục đích, tái sử dụng ảnh nền tảng,
/// ma trận phụ thuộc §3.4 và audio tùy chọn.
/// </summary>
public sealed partial class ProjectWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly JobService? _jobService;
    private readonly Func<StartProjectWorkflowRequest, CancellationToken, Task<int>>? _durableJobEnqueuer;
    private readonly WorkflowSceneJobs _sceneJobs;

    public ProjectWorkflowService(
        DbContextOptions<FrayloDbContext> dbOptions,
        JobService? jobService = null,
        Func<StartProjectWorkflowRequest, CancellationToken, Task<int>>? durableJobEnqueuer = null)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _jobService = jobService;
        _durableJobEnqueuer = durableJobEnqueuer;
        _sceneJobs = new WorkflowSceneJobs(_dbOptions);
    }

    public ProjectWorkflowService(
        DbContextOptions<FrayloDbContext> dbOptions,
        Func<StartProjectWorkflowRequest, CancellationToken, Task<int>>? durableJobEnqueuer)
        : this(dbOptions, null, durableJobEnqueuer)
    {
    }

    private static string GetConfigKey(string projectId) => $"project.workflow.config.{projectId}";
    private static string GetSceneKey(string sceneId) => $"scene.workflow.{sceneId}";

    public Task<StoryboardContent?> GetStoryboardAsync(string projectId, CancellationToken cancellationToken = default) =>
        new StoryboardContentService(_dbOptions).GetAsync(projectId, cancellationToken);

    private static async Task ValidateAssetBelongsToProjectAsync(
        FrayloDbContext dbContext,
        string projectId,
        string? assetId,
        string assetName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return;

        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);

        if (asset == null)
        {
            throw new ProjectWorkflowValidationException($"Không tìm thấy tệp {assetName} '{assetId}'.");
        }

        if (asset.ProjectId is not null && asset.ProjectId != projectId)
        {
            throw new UnauthorizedAccessException($"Tệp {assetName} '{assetId}' thuộc Project '{asset.ProjectId}', không dùng được trong Project '{projectId}'.");
        }

    }

    private static string NormalizeAndValidateGuid(string? id, string paramName)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var parsedGuid))
        {
            throw new ProjectWorkflowValidationException($"Mã định danh '{paramName}' không phải là GUID hợp lệ.");
        }

        return parsedGuid.ToString("D").ToLowerInvariant();
    }
}
