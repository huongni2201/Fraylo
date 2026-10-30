using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TiToAI.Application.Content;
using TiToAI.Application.Prompts;
using TiToAI.Contracts.Projects;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Application.Projects;

/// <summary>
/// Dịch vụ quản lý Nhóm dự án: gom Dự án cùng ngành hàng và giữ hướng dẫn prompt/biến dùng chung.
/// </summary>
public sealed class ProjectGroupService
{
    private readonly DbContextOptions<TiToAIDbContext> _dbOptions;

    public ProjectGroupService(DbContextOptions<TiToAIDbContext> dbOptions)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
    }

    public async Task<IReadOnlyList<ProjectGroupDto>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = new TiToAIDbContext(_dbOptions);

        var groups = await dbContext.ProjectGroups
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);
        var counts = await dbContext.Projects
            .AsNoTracking()
            .Where(p => p.GroupId != null)
            .GroupBy(p => p.GroupId!)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return groups
            .Select(g => ToDto(g, counts.FirstOrDefault(c => c.GroupId == g.Id)?.Count ?? 0))
            .ToList();
    }

    /// <summary>
    /// Tạo mới Nhóm (GroupId rỗng) hoặc cập nhật toàn bộ nội dung Nhóm đã có.
    /// </summary>
    public async Task<ProjectGroupDto> SaveAsync(ProjectGroupSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) throw new ProjectValidationException("Tên nhóm không được để trống.");
        if (name.Length > 100) throw new ProjectValidationException("Tên nhóm không được vượt quá 100 ký tự.");

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length > 500) throw new ProjectValidationException("Mô tả nhóm không được vượt quá 500 ký tự.");

        var guidanceError = PromptGuidance.Validate(request.ScriptGuidance, "Prompt kịch bản")
            ?? PromptGuidance.Validate(request.VideoGuidance, "Prompt video");
        if (guidanceError is not null) throw new ProjectValidationException(guidanceError);

        var groupId = string.IsNullOrWhiteSpace(request.GroupId) ? null : NormalizeAndValidateGuid(request.GroupId, nameof(request.GroupId));

        await using var dbContext = new TiToAIDbContext(_dbOptions);

        ProjectGroup? group = null;
        if (groupId is not null)
        {
            group = await dbContext.ProjectGroups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken)
                ?? throw new ProjectGroupNotFoundException(groupId);
        }

        // Tên so sánh không phân biệt hoa thường theo văn hóa; SQLite LOWER() không xử lý đúng tiếng Việt.
        var otherNames = await dbContext.ProjectGroups
            .AsNoTracking()
            .Where(g => g.Id != groupId)
            .Select(g => g.Name)
            .ToListAsync(cancellationToken);
        if (otherNames.Any(other => string.Equals(other, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ProjectValidationException($"Tên nhóm '{name}' đã tồn tại.");
        }

        var nowUtc = DateTime.UtcNow.ToString("o");
        if (group is null)
        {
            group = new ProjectGroup
            {
                Id = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                CreatedAtUtc = nowUtc
            };
            dbContext.ProjectGroups.Add(group);
        }

        var memberProjectIds = await dbContext.Projects
            .AsNoTracking()
            .Where(p => p.GroupId == group.Id)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        var originalInputs = InheritedInputs(group);
        group.ModelAssetId = await ResolveGroupAssetAsync(dbContext, group.Id, memberProjectIds, request.ModelAssetId, "ảnh mẫu", "Image", cancellationToken);
        group.ProductAssetId = await ResolveGroupAssetAsync(dbContext, group.Id, memberProjectIds, request.ProductAssetId, "ảnh sản phẩm", "Image", cancellationToken);
        group.ReadyImageAssetId = await ResolveGroupAssetAsync(dbContext, group.Id, memberProjectIds, request.ReadyImageAssetId, "ảnh có sẵn", "Image", cancellationToken);
        group.AudioAssetId = await ResolveGroupAssetAsync(dbContext, group.Id, memberProjectIds, request.AudioAssetId, "tệp âm thanh", "Audio", cancellationToken);

        group.Name = name;
        group.Description = description;
        group.ScriptGuidance = PromptGuidance.Normalize(request.ScriptGuidance) ?? string.Empty;
        group.VideoGuidance = PromptGuidance.Normalize(request.VideoGuidance) ?? string.Empty;
        group.UpdatedAtUtc = nowUtc;

        // Dự án đọc hướng dẫn/tệp của Nhóm ở từng bước: không đổi giữa run, và kết quả cũ trở thành stale.
        if (InheritedInputs(group) != originalInputs)
        {
            await EnsureNoActiveRunAsync(dbContext, memberProjectIds, cancellationToken);
            await WorkflowInputGuard.MarkProjectVideosStaleAsync(dbContext, memberProjectIds, "Prompt hoặc tệp mặc định của nhóm đã thay đổi.", cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var projectCount = await dbContext.Projects.CountAsync(p => p.GroupId == group.Id, cancellationToken);
        return ToDto(group, projectCount);
    }

    /// <summary>
    /// Xóa Nhóm. Dự án trong nhóm được giữ lại và chuyển về không nhóm (ON DELETE SET NULL của schema).
    /// </summary>
    public async Task<ProjectGroupDeleteResponseData> DeleteAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var normalizedGroupId = NormalizeAndValidateGuid(groupId, nameof(groupId));

        await using var dbContext = new TiToAIDbContext(_dbOptions);

        var group = await dbContext.ProjectGroups.FirstOrDefaultAsync(g => g.Id == normalizedGroupId, cancellationToken)
            ?? throw new ProjectGroupNotFoundException(normalizedGroupId);

        var memberProjectIds = await dbContext.Projects.AsNoTracking()
            .Where(p => p.GroupId == normalizedGroupId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        if (InheritedInputs(group) != (null, null, null, null, "", ""))
        {
            await EnsureNoActiveRunAsync(dbContext, memberProjectIds, cancellationToken);
            await WorkflowInputGuard.MarkProjectVideosStaleAsync(dbContext, memberProjectIds, "Nhóm của dự án đã bị xóa.", cancellationToken);
        }

        dbContext.ProjectGroups.Remove(group);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ProjectGroupDeleteResponseData(normalizedGroupId, true);
    }

    private static (string?, string?, string?, string?, string, string) InheritedInputs(ProjectGroup group) =>
        (group.ModelAssetId, group.ProductAssetId, group.ReadyImageAssetId, group.AudioAssetId, group.ScriptGuidance ?? "", group.VideoGuidance ?? "");

    internal static async Task EnsureNoActiveRunAsync(TiToAIDbContext dbContext, IReadOnlyCollection<string> projectIds, CancellationToken cancellationToken)
    {
        if (await WorkflowInputGuard.HasActiveRunAsync(dbContext, projectIds, cancellationToken))
        {
            throw new ProjectValidationException("Có dự án trong nhóm đang chạy hoặc tạm dừng run. Hủy hoặc chờ run hoàn tất trước khi đổi nhóm.");
        }
    }

    private static ProjectGroupDto ToDto(ProjectGroup group, int projectCount) => new(
        group.Id,
        group.Name,
        group.Description,
        group.ScriptGuidance,
        group.VideoGuidance,
        projectCount,
        group.CreatedAtUtc,
        group.UpdatedAtUtc,
        group.ModelAssetId,
        group.ProductAssetId,
        group.ReadyImageAssetId,
        group.AudioAssetId);

    /// <summary>
    /// Kiểm tra tệp mặc định của Nhóm. Chỉ nhận tệp đã thuộc Nhóm, hoặc tệp của một Project trong Nhóm
    /// (tệp đó chuyển thành tệp của Nhóm để mọi Project trong Nhóm dùng chung). Tệp thư viện chung và tệp của Project ngoài Nhóm bị từ chối.
    /// </summary>
    private static async Task<string?> ResolveGroupAssetAsync(
        TiToAIDbContext dbContext,
        string groupId,
        IReadOnlyCollection<string> memberProjectIds,
        string? assetId,
        string label,
        string expectedType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return null;

        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.Id == assetId.Trim(), cancellationToken)
            ?? throw new ProjectValidationException($"Không tìm thấy tệp {label} '{assetId}'.");
        if (!string.Equals(asset.AssetType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectValidationException($"Tệp {label} phải có loại {expectedType}.");
        }

        if (asset.GroupId == groupId) return asset.Id;
        if (asset.ProjectId is not null && memberProjectIds.Contains(asset.ProjectId))
        {
            asset.ProjectId = null;
            asset.GroupId = groupId;
            return asset.Id;
        }

        throw new ProjectValidationException(
            $"Tệp {label} thuộc dự án ngoài nhóm hoặc thư viện chung. Hãy tải lên trong nhóm hoặc chọn tệp của một dự án trong nhóm.");
    }

    private static string NormalizeAndValidateGuid(string? id, string paramName)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var parsedGuid))
        {
            throw new ProjectValidationException($"Mã định danh '{paramName}' không phải là GUID hợp lệ.");
        }

        return parsedGuid.ToString("D").ToLowerInvariant();
    }
}
