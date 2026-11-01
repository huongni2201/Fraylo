using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Fraylo.Application.Content;
using Fraylo.Application.Projects;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Scenes;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Scenes;

/// <summary>
/// Dịch vụ nghiệp vụ quản lý Cảnh (Scene CRUD, Sequence Ordering, Reorder Transactional &amp; Multi-level Isolation)
/// tuân thủ ranh giới dữ liệu kênh TikTok theo contracts.md v1.0.4 và T08b.
/// </summary>
public sealed class SceneService
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Empty",
        "Draft",
        "Queued",
        "Running",
        "Completed",
        "Failed"
    };

    private readonly DbContextOptions<FrayloDbContext> _dbOptions;

    public SceneService(DbContextOptions<FrayloDbContext> dbOptions)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
    }

    /// <summary>
    /// Lấy danh sách toàn bộ Cảnh trong Dự án theo thứ tự SequenceOrder tăng dần.
    /// </summary>
    public async Task<IReadOnlyList<SceneDto>> ListByProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var project = await dbContext.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == normalizedProjectId, cancellationToken);

        if (project is null)
        {
            throw new ProjectNotFoundException(normalizedProjectId);
        }

        var scenes = await dbContext.Scenes
            .AsNoTracking()
            .Where(s => s.ProjectId == normalizedProjectId)
            .OrderBy(s => s.SequenceOrder)
            .ToListAsync(cancellationToken);

        return scenes.Select(ToSceneDto).ToList();
    }

    /// <summary>
    /// Lấy thông tin chi tiết một Cảnh theo ProjectId và SceneId.
    /// </summary>
    public async Task<SceneDto> GetAsync(string projectId, string sceneId, CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));
        var normalizedSceneId = NormalizeAndValidateGuid(sceneId, nameof(sceneId));

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var project = await dbContext.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == normalizedProjectId, cancellationToken);

        if (project is null)
        {
            throw new ProjectNotFoundException(normalizedProjectId);
        }

        var scene = await dbContext.Scenes
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == normalizedSceneId, cancellationToken);

        if (scene is null)
        {
            throw new SceneNotFoundException(normalizedSceneId);
        }

        if (scene.ProjectId != normalizedProjectId)
        {
            throw new SceneNotFoundException(normalizedSceneId);
        }

        return ToSceneDto(scene);
    }

    /// <summary>
    /// Tạo mới một Cảnh trong Dự án, tự động gán SequenceOrder = (max hiện tại) + 1.
    /// </summary>
    public async Task<SceneCreateResponseData> CreateAsync(SceneCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var project = await dbContext.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == normalizedProjectId, cancellationToken);

        if (project is null)
        {
            throw new ProjectNotFoundException(normalizedProjectId);
        }

        // Validate duration nếu có
        var duration = request.TargetDurationSeconds ?? 5;
        if (duration <= 0)
        {
            throw new SceneValidationException("Thời lượng mục tiêu của cảnh phải lớn hơn 0 giây.");
        }

        // Validate Asset references nếu có
        string? audioAssetId = await ValidateAssetReferenceAsync(dbContext, request.AudioAssetId, "Audio", cancellationToken);
        string? imageAssetId = await ValidateAssetReferenceAsync(dbContext, request.ImageAssetId, "Image", cancellationToken);
        string? videoAssetId = await ValidateAssetReferenceAsync(dbContext, request.VideoAssetId, "Video", cancellationToken);

        // Validate PromptText
        string? promptText = null;
        if (!string.IsNullOrWhiteSpace(request.PromptText))
        {
            promptText = request.PromptText.Trim();
            if (promptText.Length > 4000)
            {
                throw new SceneValidationException("Văn bản prompt không được vượt quá 4000 ký tự.");
            }
        }

        // Tính toán SequenceOrder = max + 1
        var currentMax = await dbContext.Scenes
            .Where(s => s.ProjectId == normalizedProjectId)
            .MaxAsync(s => (int?)s.SequenceOrder, cancellationToken) ?? 0;

        var nextSequence = currentMax + 1;

        // Tên cảnh: nếu không truyền hoặc rỗng thì tự sinh mặc định "Cảnh {sequence}"
        string sceneName;
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            sceneName = $"Cảnh {nextSequence}";
        }
        else
        {
            var trimmedName = request.Name.Trim();
            if (trimmedName.Length > 100)
            {
                throw new SceneValidationException("Tên cảnh không được vượt quá 100 ký tự.");
            }
            sceneName = trimmedName;
        }

        var newId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var nowUtc = DateTime.UtcNow.ToString("o");

        var scene = new Scene
        {
            Id = newId,
            ProjectId = normalizedProjectId,
            SequenceOrder = nextSequence,
            Name = sceneName,
            TargetDurationSeconds = duration,
            PromptText = promptText,
            Status = "Draft",
            AudioAssetId = audioAssetId,
            ImageAssetId = imageAssetId,
            VideoAssetId = videoAssetId,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

        dbContext.Scenes.Add(scene);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new SceneCreateResponseData(scene.Id, scene.SequenceOrder);
    }

    /// <summary>
    /// Cập nhật thông tin Cảnh (tên, prompt, duration, status, asset references).
    /// </summary>
    public async Task<SceneDto> UpdateAsync(SceneUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));
        var normalizedSceneId = NormalizeAndValidateGuid(request.SceneId, nameof(request.SceneId));

        await using var dbContext = new FrayloDbContext(_dbOptions);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        var dbConn = dbContext.Database.GetDbConnection();

        IDbContextTransaction transaction;
        if (dbConn is SqliteConnection sqliteConn)
        {
            var sqliteTx = sqliteConn.BeginTransaction(deferred: false);
            transaction = await dbContext.Database.UseTransactionAsync(sqliteTx, cancellationToken)
                ?? throw new InvalidOperationException("Không thể khởi tạo DbContextTransaction từ SqliteTransaction.");
        }
        else
        {
            transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        }

        await using (transaction)
        {
            try
            {
                var scene = await dbContext.Scenes
                    .FirstOrDefaultAsync(s => s.Id == normalizedSceneId, cancellationToken);

                if (scene is null)
                {
                    throw new SceneNotFoundException(normalizedSceneId);
                }

                if (scene.ProjectId != normalizedProjectId)
                {
                    throw new SceneNotFoundException(normalizedSceneId);
                }

                if (!string.IsNullOrEmpty(request.ExpectedRevision))
                {
                    if (!string.Equals(scene.UpdatedAtUtc, request.ExpectedRevision, StringComparison.Ordinal))
                    {
                        throw new SceneConcurrencyConflictException(
                            $"Cảnh '{normalizedSceneId}' đã bị chỉnh sửa bởi một phiên khác (phiên bản hiện tại '{scene.UpdatedAtUtc}', mong đợi '{request.ExpectedRevision}').");
                    }
                }

                bool isModified = false;
                var originalInputs = (scene.PromptText, scene.TargetDurationSeconds, scene.AudioAssetId, scene.ImageAssetId);
                var originalVideoAssetId = scene.VideoAssetId;

                if (request.Name is not null)
                {
                    var trimmedName = request.Name.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedName))
                    {
                        throw new SceneValidationException("Tên cảnh không được để trống.");
                    }
                    if (trimmedName.Length > 100)
                    {
                        throw new SceneValidationException("Tên cảnh không được vượt quá 100 ký tự.");
                    }
                    scene.Name = trimmedName;
                    isModified = true;
                }

                if (request.PromptText is not null)
                {
                    var trimmedPrompt = request.PromptText.Trim();
                    if (trimmedPrompt.Length > 4000)
                    {
                        throw new SceneValidationException("Văn bản prompt không được vượt quá 4000 ký tự.");
                    }
                    scene.PromptText = string.IsNullOrWhiteSpace(trimmedPrompt) ? null : trimmedPrompt;
                    isModified = true;
                }

                if (request.TargetDurationSeconds.HasValue)
                {
                    if (request.TargetDurationSeconds.Value <= 0)
                    {
                        throw new SceneValidationException("Thời lượng mục tiêu của cảnh phải lớn hơn 0 giây.");
                    }
                    scene.TargetDurationSeconds = request.TargetDurationSeconds.Value;
                    isModified = true;
                }

                if (request.Status is not null)
                {
                    var trimmedStatus = request.Status.Trim();
                    if (!AllowedStatuses.Contains(trimmedStatus))
                    {
                        throw new SceneValidationException($"Trạng thái cảnh '{trimmedStatus}' không hợp lệ.");
                    }
                    scene.Status = trimmedStatus;
                    isModified = true;
                }

                if (request.AudioAssetId is not null)
                {
                    scene.AudioAssetId = await ValidateAssetReferenceAsync(dbContext, request.AudioAssetId, "Audio", cancellationToken);
                    isModified = true;
                }

                if (request.ImageAssetId is not null)
                {
                    scene.ImageAssetId = await ValidateAssetReferenceAsync(dbContext, request.ImageAssetId, "Image", cancellationToken);
                    isModified = true;
                }

                if (request.VideoAssetId is not null)
                {
                    scene.VideoAssetId = await ValidateAssetReferenceAsync(dbContext, request.VideoAssetId, "Video", cancellationToken);
                    isModified = true;
                }

                // Đầu vào video (prompt/ảnh/audio/thời lượng) đổi qua form Cảnh phải theo cùng quy tắc với workflow.update_scene_input.
                if ((scene.PromptText, scene.TargetDurationSeconds, scene.AudioAssetId, scene.ImageAssetId) != originalInputs)
                {
                    if (await WorkflowInputGuard.IsRunRunningAsync(dbContext, scene.ProjectId, cancellationToken))
                    {
                        throw new SceneValidationException("Tạm dừng run trước khi thay đổi input của một cảnh.");
                    }
                    if (await WorkflowInputGuard.HasActiveSceneJobAsync(dbContext, scene.Id, cancellationToken))
                    {
                        throw new SceneValidationException("Đợi các job đang hoạt động hoặc chưa được đối soát xong trước khi sửa input của cảnh.");
                    }
                    if (scene.VideoAssetId != null && scene.VideoAssetId == originalVideoAssetId)
                    {
                        // Như ma trận §3.4: chỉ đổi prompt thì làm lại video; đổi ảnh/audio/thời lượng thì làm lại cả prompt video.
                        string[] staleTypes = scene.PromptText != originalInputs.PromptText &&
                            (scene.TargetDurationSeconds, scene.AudioAssetId, scene.ImageAssetId) == (originalInputs.TargetDurationSeconds, originalInputs.AudioAssetId, originalInputs.ImageAssetId)
                            ? [JobTaskType.VideoRender]
                            : [JobTaskType.VideoPrompt, JobTaskType.VideoRender];
                        await WorkflowInputGuard.MarkSceneVideoStaleAsync(dbContext, scene.Id, "Input của cảnh đã thay đổi. Cần tạo lại video.", staleTypes, cancellationToken);
                    }
                }

                if (isModified)
                {
                    var nowUtc = DateTime.UtcNow.ToString("o");
                    if (string.Equals(nowUtc, scene.UpdatedAtUtc, StringComparison.Ordinal))
                    {
                        nowUtc = DateTime.UtcNow.AddTicks(10).ToString("o");
                    }
                    scene.UpdatedAtUtc = nowUtc;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                return ToSceneDto(scene);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // Bảo toàn ngoại lệ gốc
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Xóa một Cảnh với Strict Active Job Guard và tự động đánh số lại SequenceOrder các cảnh còn lại.
    /// </summary>
    public async Task<SceneDeleteResponseData> DeleteAsync(string projectId, string sceneId, CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));
        var normalizedSceneId = NormalizeAndValidateGuid(sceneId, nameof(sceneId));

        await using var dbContext = new FrayloDbContext(_dbOptions);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        var dbConn = dbContext.Database.GetDbConnection();

        IDbContextTransaction transaction;
        if (dbConn is SqliteConnection sqliteConn)
        {
            var sqliteTx = sqliteConn.BeginTransaction(deferred: false);
            transaction = await dbContext.Database.UseTransactionAsync(sqliteTx, cancellationToken)
                ?? throw new InvalidOperationException("Không thể khởi tạo DbContextTransaction từ SqliteTransaction.");
        }
        else
        {
            transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        }

        await using (transaction)
        {
            try
            {
                var scene = await dbContext.Scenes
                    .FirstOrDefaultAsync(s => s.Id == normalizedSceneId, cancellationToken);

                if (scene is null)
                {
                    throw new SceneNotFoundException(normalizedSceneId);
                }

                if (scene.ProjectId != normalizedProjectId)
                {
                    throw new SceneNotFoundException(normalizedSceneId);
                }

                // 1. Strict Nonterminal & Unreconciled Side-Effect Guard cho Scene:
                bool hasActiveOrUnreconciledJobs = await dbContext.Jobs.AnyAsync(j =>
                    j.SceneId == normalizedSceneId &&
                    (
                        j.State == "Queued" ||
                        j.State == "Reserved" ||
                        j.State == "Running" ||
                        j.State == "AwaitingUser" ||
                        (j.SideEffectSubmitted == 1 && (j.ResultReference == null || j.ResultReference.Trim() == "" || j.State == "AwaitingUser"))
                    ),
                    cancellationToken);

                if (hasActiveOrUnreconciledJobs)
                {
                    throw new SceneConcurrencyConflictException(
                        "Không thể xóa cảnh trong khi có tác vụ đang chờ, đang chạy hoặc tác vụ có tác dụng phụ (side-effect) chưa đối soát xong.");
                }

                // 2. Xóa Scene:
                dbContext.Scenes.Remove(scene);
                await dbContext.SaveChangesAsync(cancellationToken);

                // 3. Tự động đánh số lại SequenceOrder liên tục 1, 2, 3... cho các cảnh còn lại trong Project
                var remainingScenes = await dbContext.Scenes
                    .Where(s => s.ProjectId == normalizedProjectId)
                    .OrderBy(s => s.SequenceOrder)
                    .ToListAsync(cancellationToken);

                var nowUtc = DateTime.UtcNow.ToString("o");
                for (int i = 0; i < remainingScenes.Count; i++)
                {
                    remainingScenes[i].SequenceOrder = i + 1;
                    remainingScenes[i].UpdatedAtUtc = nowUtc;
                }

                await dbContext.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // Bảo toàn ngoại lệ gốc
                }
                throw;
            }
        }

        return new SceneDeleteResponseData(normalizedSceneId, true);
    }

    /// <summary>
    /// Sắp xếp lại thứ tự (reorder) toàn bộ Cảnh trong Dự án theo danh sách sceneIds.
    /// Xác thực tập hợp khớp chính xác 100%, thực thi trong write transaction serialize và dùng 2-pass sequence để phòng ngừa xung đột unique.
    /// </summary>
    public async Task<SceneReorderResponseData> ReorderAsync(SceneReorderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));

        if (request.SceneIds is null || request.SceneIds.Count == 0)
        {
            throw new SceneValidationException("Danh sách mã cảnh cần sắp xếp lại không được để trống.");
        }

        var normalizedSceneIds = request.SceneIds
            .Select(id => NormalizeAndValidateGuid(id, "sceneId"))
            .ToList();

        // Kiểm tra danh sách không chứa ID trùng lặp
        if (normalizedSceneIds.Distinct().Count() != normalizedSceneIds.Count)
        {
            throw new SceneValidationException("Danh sách sắp xếp lại chứa mã cảnh bị trùng lặp.");
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        var dbConn = dbContext.Database.GetDbConnection();

        IDbContextTransaction transaction;
        if (dbConn is SqliteConnection sqliteConn)
        {
            var sqliteTx = sqliteConn.BeginTransaction(deferred: false);
            transaction = await dbContext.Database.UseTransactionAsync(sqliteTx, cancellationToken)
                ?? throw new InvalidOperationException("Không thể khởi tạo DbContextTransaction từ SqliteTransaction.");
        }
        else
        {
            transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        }

        await using (transaction)
        {
            try
            {
                var project = await dbContext.Projects
                    .FirstOrDefaultAsync(p => p.Id == normalizedProjectId, cancellationToken);

                if (project is null)
                {
                    throw new ProjectNotFoundException(normalizedProjectId);
                }

                // Kiểm tra optimistic concurrency cho dự án
                if (!string.IsNullOrEmpty(request.ExpectedProjectRevision))
                {
                    if (!string.Equals(project.UpdatedAtUtc, request.ExpectedProjectRevision, StringComparison.Ordinal))
                    {
                        throw new SceneConcurrencyConflictException(
                            $"Dự án '{normalizedProjectId}' đã bị chỉnh sửa bởi một phiên khác (phiên bản hiện tại '{project.UpdatedAtUtc}', mong đợi '{request.ExpectedProjectRevision}').");
                    }
                }

                var existingScenes = await dbContext.Scenes
                    .Where(s => s.ProjectId == normalizedProjectId)
                    .ToListAsync(cancellationToken);

                // Xác thực tập hợp phải khớp chính xác 100% với danh sách hiện tại của Project
                if (existingScenes.Count != normalizedSceneIds.Count)
                {
                    throw new SceneValidationException($"Số lượng cảnh yêu cầu sắp xếp ({normalizedSceneIds.Count}) không khớp với số cảnh hiện có trong dự án ({existingScenes.Count}).");
                }

                var sceneMap = existingScenes.ToDictionary(s => s.Id);
                foreach (var id in normalizedSceneIds)
                {
                    if (!sceneMap.ContainsKey(id))
                    {
                        throw new SceneValidationException($"Mã cảnh '{id}' không thuộc dự án hoặc không tìm thấy.");
                    }
                }

                // Kiểm tra optimistic concurrency cho từng scene nếu có request.ExpectedSceneRevisions
                if (request.ExpectedSceneRevisions is not null && request.ExpectedSceneRevisions.Count > 0)
                {
                    var normalizedExpectedRevisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in request.ExpectedSceneRevisions)
                    {
                        if (Guid.TryParse(kvp.Key, out var parsedGuid))
                        {
                            normalizedExpectedRevisions[parsedGuid.ToString("D").ToLowerInvariant()] = kvp.Value;
                        }
                        else
                        {
                            normalizedExpectedRevisions[kvp.Key] = kvp.Value;
                        }
                    }

                    foreach (var sc in existingScenes)
                    {
                        if (normalizedExpectedRevisions.TryGetValue(sc.Id, out var expectedRev) && !string.IsNullOrEmpty(expectedRev))
                        {
                            if (!string.Equals(sc.UpdatedAtUtc, expectedRev, StringComparison.Ordinal))
                            {
                                throw new SceneConcurrencyConflictException(
                                    $"Cảnh '{sc.Id}' đã bị chỉnh sửa bởi một phiên khác (phiên bản hiện tại '{sc.UpdatedAtUtc}', mong đợi '{expectedRev}').");
                            }
                        }
                    }
                }

                // 2-Pass Update để hoàn toàn ngăn ngừa collision trên unique index nếu có
                // Pass 1: Gán sequence tạm thời mang dấu âm
                for (int i = 0; i < normalizedSceneIds.Count; i++)
                {
                    sceneMap[normalizedSceneIds[i]].SequenceOrder = -(i + 1);
                }
                await dbContext.SaveChangesAsync(cancellationToken);

                // Pass 2: Gán sequence chính thức từ 1, 2, 3...
                var nowUtc = DateTime.UtcNow.ToString("o");
                if (string.Equals(nowUtc, project.UpdatedAtUtc, StringComparison.Ordinal))
                {
                    nowUtc = DateTime.UtcNow.AddTicks(10).ToString("o");
                }

                for (int i = 0; i < normalizedSceneIds.Count; i++)
                {
                    var sc = sceneMap[normalizedSceneIds[i]];
                    sc.SequenceOrder = i + 1;
                    sc.UpdatedAtUtc = nowUtc;
                }

                project.UpdatedAtUtc = nowUtc;
                await dbContext.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                var reorderedDtos = normalizedSceneIds
                    .Select(id => ToSceneDto(sceneMap[id]))
                    .ToList();

                return new SceneReorderResponseData(true, reorderedDtos);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // Bảo toàn ngoại lệ gốc
                }
                throw;
            }
        }
    }

    private static async Task<string?> ValidateAssetReferenceAsync(
        FrayloDbContext dbContext,
        string? assetId,
        string expectedAssetType,
        CancellationToken cancellationToken)
    {
        if (assetId is null)
        {
            return null;
        }

        var trimmed = assetId.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var normalizedAssetId = NormalizeAndValidateGuid(trimmed, "assetId");

        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == normalizedAssetId, cancellationToken);

        if (asset is null)
        {
            throw new SceneValidationException($"Không tìm thấy tệp tài nguyên '{normalizedAssetId}'.");
        }

        if (!string.Equals(asset.AssetType, expectedAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new SceneValidationException($"Tài nguyên '{normalizedAssetId}' là loại '{asset.AssetType}', không khớp với loại yêu cầu '{expectedAssetType}'.");
        }

        return normalizedAssetId;
    }

    private static SceneDto ToSceneDto(Scene s) => new(
        SceneId: s.Id,
        ProjectId: s.ProjectId,
        Name: s.Name,
        SceneSequence: s.SequenceOrder,
        TargetDurationSeconds: Convert.ToInt32(s.TargetDurationSeconds),
        PromptText: s.PromptText,
        Status: s.Status,
        AudioAssetId: s.AudioAssetId,
        ImageAssetId: s.ImageAssetId,
        VideoAssetId: s.VideoAssetId,
        CreatedAtUtc: s.CreatedAtUtc,
        UpdatedAtUtc: s.UpdatedAtUtc,
        Revision: s.UpdatedAtUtc);

    private static string NormalizeAndValidateGuid(string? id, string paramName)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var parsedGuid))
        {
            throw new SceneValidationException($"Mã định danh '{paramName}' không phải là GUID hợp lệ.");
        }

        return parsedGuid.ToString("D").ToLowerInvariant();
    }
}
