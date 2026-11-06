using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Fraylo.Application.Content;
using Fraylo.Contracts.Projects;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Projects;

/// <summary>
/// Dịch vụ nghiệp vụ quản lý Dự án (Project CRUD, Safe Detach &amp; Active Job Guard)
/// tuân thủ ranh giới dữ liệu kênh TikTok theo contracts.md v1.0.4 và T08a.
/// </summary>
public sealed class ProjectService
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Draft",
        "InProgress",
        "Completed"
    };

    private readonly DbContextOptions<FrayloDbContext> _dbOptions;

    public ProjectService(DbContextOptions<FrayloDbContext> dbOptions)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
    }

    /// <summary>
    /// Lists all project aggregates for the project-first UI.
    /// </summary>
    public async Task<IReadOnlyList<ProjectSummaryDto>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = new FrayloDbContext(_dbOptions);

        return await dbContext.Projects
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAtUtc)
            .Select(p => new ProjectSummaryDto(
                p.Id,
                p.Name,
                p.Brief,
                p.Status,
                dbContext.Scenes.Count(s => s.ProjectId == p.Id),
                dbContext.Scenes.Any(s => s.ProjectId == p.Id && s.VideoAssetId != null),
                p.CreatedAtUtc,
                p.UpdatedAtUtc,
                dbContext.Scenes
                    .Where(s => s.ProjectId == p.Id && s.VideoAssetId != null)
                    .OrderBy(s => s.SequenceOrder)
                    .Select(s => s.VideoAssetId)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Lấy thông tin chi tiết một Dự án theo Project ID.
    /// </summary>
    public async Task<ProjectDetailDto> GetAsync(string projectId, CancellationToken cancellationToken = default)
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

        return ToDetail(project);
    }

    /// <summary>
    /// Tạo mới một Dự án thuộc Không gian làm việc với trạng thái ban đầu là "Draft".
    /// </summary>
    public async Task<ProjectCreateResponseData> CreateAsync(ProjectCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (name, description) = ValidateProjectFields(request.Name, request.Description);

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var newId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var nowUtc = DateTime.UtcNow.ToString("o");

        var project = new Project
        {
            Id = newId,
            Name = name,
            Brief = description ?? string.Empty,
            Status = "Draft",
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ProjectCreateResponseData(project.Id);
    }

    /// <summary>
    /// Cập nhật thông tin Dự án (tên, mô tả, trạng thái).
    /// </summary>
    public async Task<ProjectDetailDto> UpdateAsync(ProjectUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var project = await dbContext.Projects
            .FirstOrDefaultAsync(p => p.Id == normalizedProjectId, cancellationToken);

        if (project is null)
        {
            throw new ProjectNotFoundException(normalizedProjectId);
        }

        bool isModified = false;

        if (request.Name is not null)
        {
            var trimmedName = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                throw new ProjectValidationException("Tên dự án không được để trống.");
            }
            if (trimmedName.Length > 100)
            {
                throw new ProjectValidationException("Tên dự án không được vượt quá 100 ký tự.");
            }
            project.Name = trimmedName;
            isModified = true;
        }

        if (request.Description is not null)
        {
            var trimmedDesc = request.Description.Trim();
            if (trimmedDesc.Length > 500)
            {
                throw new ProjectValidationException("Mô tả dự án không được vượt quá 500 ký tự.");
            }
            project.Brief = string.IsNullOrWhiteSpace(trimmedDesc) ? string.Empty : trimmedDesc;
            isModified = true;
        }

        if (request.Status is not null)
        {
            var trimmedStatus = request.Status.Trim();
            if (!AllowedStatuses.Contains(trimmedStatus))
            {
                throw new ProjectValidationException($"Trạng thái dự án '{trimmedStatus}' không hợp lệ. Chỉ chấp nhận Draft, InProgress, hoặc Completed.");
            }
            project.Status = trimmedStatus;
            isModified = true;
        }

        if (isModified)
        {
            project.UpdatedAtUtc = DateTime.UtcNow.ToString("o");
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return ToDetail(project);
    }

    /// <summary>
    /// Xóa Dự án với Strict Active Job Guard và Safe Asset Detach trong cùng một write transaction serialize.
    /// </summary>
    public async Task<ProjectDeleteResponseData> DeleteAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));

        await using var dbContext = new FrayloDbContext(_dbOptions);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        var dbConn = dbContext.Database.GetDbConnection();

        var transaction = await dbContext.Database.BeginImmediateAsync(cancellationToken).ConfigureAwait(false);

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

                // 1. Strict Nonterminal & Unreconciled Side-Effect Job Guard:
                // Chặn xóa nếu có bất kỳ Job nào thuộc dự án (cả cấp Project với SceneId = null lẫn cấp Scene)
                // đang ở trạng thái chưa kết thúc (Queued, Reserved, Running, AwaitingUser)
                // hoặc job có SideEffectSubmitted == 1 mà chưa đối soát xong (ResultReference null/rỗng hoặc đang ở AwaitingUser).
                bool hasActiveOrUnreconciledJobs = await dbContext.Jobs
                    .Where(j => j.ProjectId == normalizedProjectId)
                    .Where(j =>
                        j.State == "Queued" ||
                        j.State == "Reserved" ||
                        j.State == "Running" ||
                        j.State == "AwaitingUser" ||
                        (j.SideEffectSubmitted == 1 && (j.ResultReference == null || j.ResultReference.Trim() == "" || j.State == "AwaitingUser"))
                    )
                    .AnyAsync(cancellationToken);

                if (hasActiveOrUnreconciledJobs)
                {
                    throw new ProjectConcurrencyConflictException(
                        "Không thể xóa dự án trong khi có tác vụ thuộc dự án hoặc các cảnh đang chờ, đang chạy hoặc tác vụ có tác dụng phụ (side-effect) chưa đối soát xong.");
                }

                // 2. Safe Asset Detach:
                // Bỏ liên kết Asset khỏi Project trước khi xóa
                // để giải phóng composite foreign key ON DELETE NO ACTION trước khi cascade xóa Scene.
                await dbContext.Assets
                    .Where(a => a.ProjectId == normalizedProjectId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.ProjectId, (string?)null), cancellationToken);

                // 3. Xóa Project:
                // ON DELETE CASCADE của SQLite sẽ tự động xóa các Scene và Job đã kết thúc thuộc Scene của Project.
                dbContext.Projects.Remove(project);
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
                    // Giữ nguyên ngoại lệ gốc
                }
                throw;
            }
        }

        return new ProjectDeleteResponseData(normalizedProjectId, true);
    }

    private static ProjectDetailDto ToDetail(Project project) => new(
        project.Id,
        project.Name,
        project.Brief,
        project.Status,
        project.CreatedAtUtc,
        project.UpdatedAtUtc);

    private static (string Name, string? Description) ValidateProjectFields(string? name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ProjectValidationException("Tên dự án không được để trống.");
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > 100)
        {
            throw new ProjectValidationException("Tên dự án không được vượt quá 100 ký tự.");
        }

        string? trimmedDesc = null;
        if (!string.IsNullOrWhiteSpace(description))
        {
            trimmedDesc = description.Trim();
            if (trimmedDesc.Length > 500)
            {
                throw new ProjectValidationException("Mô tả dự án không được vượt quá 500 ký tự.");
            }
        }

        return (trimmedName, trimmedDesc);
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
