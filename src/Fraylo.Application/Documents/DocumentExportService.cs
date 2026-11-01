using System.IO;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Scenes;
using Fraylo.Contracts.Documents;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Application.Documents;

public class DocumentExportException : Exception
{
    public DocumentExportException(string message) : base(message) { }
    public DocumentExportException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Dịch vụ xuất kịch bản và prompt sang tài liệu văn bản (TXT UTF-8)
/// theo docs/plans/implementation/T09c-document-export.md và SETTINGS_PLAN.md §2–3.
/// </summary>
public sealed class DocumentExportService
{
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly StorageRootRegistry _rootRegistry;
    private readonly AssetFileStore _fileStore;

    public DocumentExportService(
        DbContextOptions<FrayloDbContext> dbOptions,
        StorageRootRegistry rootRegistry,
        AssetFileStore fileStore)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _rootRegistry = rootRegistry ?? throw new ArgumentNullException(nameof(rootRegistry));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
    }

    /// <summary>
    /// Xuất nội dung Cảnh (kịch bản, prompt, thời lượng) thành tệp TXT UTF-8 trong thư mục Tài liệu (Documents Root),
    /// lưu trữ metadata Asset Document trong cơ sở dữ liệu.
    /// </summary>
    public async Task<DocumentExportResponse> ExportDocumentAsync(
        DocumentExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sceneId = NormalizeGuid(request.SceneId, nameof(request.SceneId));

        if (!string.Equals(request.Format, "txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new DocumentExportException($"Định dạng xuất '{request.Format}' hiện chưa được hỗ trợ trong MVP. Chỉ hỗ trợ định dạng 'txt'.");
        }

        // Chốt Documents Root ID hiệu lực ngay tại thời điểm bắt đầu thao tác xuất
        var currentRoots = await _rootRegistry.GetCurrentRootsAsync(cancellationToken).ConfigureAwait(false);
        var documentsRootId = currentRoots.DocumentsRootId;
        var documentsRootPath = currentRoots.DocumentsPath;

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var scene = await dbContext.Scenes
            .Include(s => s.Project)
            .FirstOrDefaultAsync(s => s.Id == sceneId, cancellationToken)
            .ConfigureAwait(false);

        if (scene is null)
        {
            throw new SceneNotFoundException(sceneId);
        }

        var projectName = scene.Project?.Name ?? "Dự án Fraylo";
        var displayName = BuildSafeDisplayName(request.CustomFileName, scene.SequenceOrder, scene.Name);

        var content = BuildDocumentContent(projectName, scene);
        var contentBytes = Encoding.UTF8.GetBytes(content);

        var assetId = Guid.NewGuid().ToString("N");
        var relativePath = $"projects/{scene.ProjectId}/documents/{assetId}.txt";

        using var memoryStream = new MemoryStream(contentBytes);
        var saveResult = await _fileStore.SaveFileAsync(
            documentsRootPath,
            relativePath,
            memoryStream,
            cancellationToken
        ).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToString("O");
        var asset = new Asset
        {
            Id = assetId,
            ProjectId = scene.ProjectId,
            AssetType = "Document",
            StorageRootId = documentsRootId,
            RelativePath = relativePath,
            ByteSize = saveResult.ByteSize,
            Sha256Hash = saveResult.Sha256Hash,
            Status = "Linked",
            MimeType = "text/plain; charset=utf-8",
            CreatedAtUtc = now
        };

        dbContext.Assets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new DocumentExportResponse(
            AssetId: assetId,
            DisplayName: displayName,
            StorageRootId: documentsRootId,
            RelativePath: relativePath,
            ByteSize: saveResult.ByteSize
        );
    }

    private static string BuildDocumentContent(string projectName, Scene scene)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"DỰ ÁN: {projectName}");
        sb.AppendLine($"CẢNH SỐ: {scene.SequenceOrder} - {scene.Name}");
        sb.AppendLine($"THỜI LƯỢNG DỰ KIẾN: {scene.TargetDurationSeconds} giây");
        sb.AppendLine($"TRẠNG THÁI: {scene.Status}");
        sb.AppendLine($"NGÀY XUẤT (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine("[NỘI DUNG KỊCH BẢN & LỜI THOẠI / PROMPT]");
        sb.AppendLine(string.IsNullOrWhiteSpace(scene.PromptText) ? "(Cảnh này chưa có nội dung kịch bản hoặc prompt)" : scene.PromptText);
        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("Xuất tự động từ Fraylo — Ứng dụng sáng tạo video ngắn cục bộ theo chuẩn SETTINGS_PLAN §2.");
        return sb.ToString();
    }

    private static string BuildSafeDisplayName(string? customFileName, int sequenceOrder, string sceneName)
    {
        if (!string.IsNullOrWhiteSpace(customFileName))
        {
            var cleaned = CleanFileName(customFileName);
            if (!cleaned.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                cleaned += ".txt";
            }
            return cleaned;
        }

        var safeSceneName = CleanFileName(sceneName);
        if (string.IsNullOrWhiteSpace(safeSceneName))
        {
            safeSceneName = $"Canh_{sequenceOrder}";
        }

        return $"Canh_{sequenceOrder}_{safeSceneName}.txt";
    }

    private static string CleanFileName(string rawName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in rawName)
        {
            if (Array.IndexOf(invalidChars, ch) < 0 && ch != '/' && ch != '\\')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }
        var cleaned = sb.ToString().Trim();
        return string.IsNullOrEmpty(cleaned) ? "document" : cleaned;
    }

    private static string NormalizeGuid(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Tham số '{paramName}' không được để trống.", paramName);
        }

        if (Guid.TryParse(value, out var parsed))
        {
            return parsed.ToString("D").ToLowerInvariant();
        }

        return value.Trim();
    }
}
