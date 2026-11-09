using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Content;

public sealed record StoryboardShot(decimal StartSeconds, decimal EndSeconds, string Description, string ImagePrompt, string? ImageAssetId = null, string? MotionPrompt = null);

public sealed record StoryboardContent(int DurationSeconds, IReadOnlyList<StoryboardShot> Shots);

/// <summary>Lưu storyboard của một video sau khi kiểm tra nội dung và thời lượng.</summary>
public sealed class StoryboardContentService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    internal const int CompactGridSchemaVersion = 2;

    private sealed record CompactGridResponse(IReadOnlyList<CompactGridShot?>? Shots);
    private sealed record CompactGridShot(string? Description, string? ImagePrompt, string? MotionPrompt);

    private readonly DbContextOptions<FrayloDbContext> _dbOptions;

    public StoryboardContentService(DbContextOptions<FrayloDbContext> dbOptions) => _dbOptions = dbOptions;

    public async Task<StoryboardContent?> GetAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(projectId, out var projectGuid))
            throw new ProjectWorkflowValidationException("Project không hợp lệ.");
        var key = $"project.storyboard.{projectGuid:D}".ToLowerInvariant();
        await using var db = new FrayloDbContext(_dbOptions);
        var value = await db.AppSettings.AsNoTracking().Where(item => item.Key == key)
            .Select(item => item.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return value == null ? null : JsonSerializer.Deserialize<StoryboardContent>(value, JsonOptions);
    }

    /// <summary>Lệch cho phép giữa thời lượng AI đặt và thời lượng cấu hình (VIDEO_TWO_FLOW_SPLIT D3; chỉnh sau khi thử Muse thật).</summary>
    public const int DurationToleranceSeconds = 2;

    /// <summary>
    /// Kiểm tra storyboard của luồng một video: các đoạn liên tiếp từ 0, kết thúc tại thời lượng AI đặt,
    /// thời lượng đó nằm trong dung sai quanh thời lượng cấu hình. Đoạn không cần prompt ảnh riêng.
    /// </summary>
    public static StoryboardContent Parse(string text, int durationSeconds) => Parse(text, durationSeconds, compactGridShotCount: 0);

    private static StoryboardContent Parse(string text, int durationSeconds, int compactGridShotCount)
    {
        StoryboardContent? storyboard;
        try
        {
            var payload = ContentCandidateParser.ExtractJsonPayload(text);
            if (compactGridShotCount > 0)
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schemaVersion", out var version))
                {
                    if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value != CompactGridSchemaVersion)
                        throw new ProjectWorkflowValidationException($"Storyboard 5×5 yêu cầu schemaVersion {CompactGridSchemaVersion}.");
                    return ReadCompactGrid(root, durationSeconds, compactGridShotCount);
                }
                storyboard = root.Deserialize<StoryboardContent>(JsonOptions);
            }
            else
            {
                storyboard = JsonSerializer.Deserialize<StoryboardContent>(payload, JsonOptions);
            }
        }
        catch (JsonException ex)
        {
            var head = text.Trim();
            if (head.Length > 120) head = head[..120] + "…";
            throw new ProjectWorkflowValidationException($"Storyboard không phải JSON hợp lệ: {ex.Message} Phản hồi bắt đầu: \"{head}\"");
        }

        if (storyboard == null || durationSeconds <= 0 || storyboard.DurationSeconds <= 0 ||
            Math.Abs(storyboard.DurationSeconds - durationSeconds) > DurationToleranceSeconds ||
            storyboard.Shots == null || storyboard.Shots.Count == 0 || storyboard.Shots.Count > 30)
            throw new ProjectWorkflowValidationException("Storyboard thiếu phân cảnh hoặc thời lượng lệch quá dung sai so với cấu hình.");

        var end = 0m;
        foreach (var shot in storyboard.Shots)
        {
            if (shot == null || shot.StartSeconds != end || shot.EndSeconds <= end ||
                shot.EndSeconds > storyboard.DurationSeconds || string.IsNullOrWhiteSpace(shot.Description) ||
                (compactGridShotCount == 0 && shot.EndSeconds != decimal.Truncate(shot.EndSeconds)))
                throw new ProjectWorkflowValidationException("Phân cảnh phải liên tiếp, có thời lượng và mô tả hợp lệ.");
            end = shot.EndSeconds;
        }

        if (end != storyboard.DurationSeconds)
            throw new ProjectWorkflowValidationException("Tổng thời lượng phân cảnh không khớp thời lượng video.");
        return storyboard;
    }

    private static StoryboardContent ReadCompactGrid(JsonElement root, int durationSeconds, int shotCount)
    {
        var shots = root.Deserialize<CompactGridResponse>(JsonOptions)?.Shots;
        if (durationSeconds <= 0)
            throw new ProjectWorkflowValidationException("Thời lượng storyboard phải lớn hơn 0.");
        if (shots?.Count != shotCount)
            throw new ProjectWorkflowValidationException($"Storyboard phải có đúng {shotCount} shot, nhận {shots?.Count ?? 0}.");

        var timeline = new List<StoryboardShot>(shotCount);
        for (int i = 0; i < shots.Count; i++)
        {
            var shot = shots[i];
            if (shot == null || string.IsNullOrWhiteSpace(shot.Description) ||
                string.IsNullOrWhiteSpace(shot.ImagePrompt) || string.IsNullOrWhiteSpace(shot.MotionPrompt))
                throw new ProjectWorkflowValidationException($"Ô {i + 1} của storyboard cần có description, imagePrompt và motionPrompt.");
            timeline.Add(new StoryboardShot(
                i * durationSeconds / (decimal)shotCount, (i + 1) * durationSeconds / (decimal)shotCount,
                shot.Description.Trim(), shot.ImagePrompt.Trim(), MotionPrompt: shot.MotionPrompt.Trim()));
        }
        return new StoryboardContent(durationSeconds, timeline);
    }

    /// <summary>
    /// Kiểm tra storyboard lưới: mỗi ô tương ứng một shot theo thứ tự trái sang phải, trên xuống dưới.
    /// mốc liên tiếp từ 0 đến đúng thời lượng yêu cầu (không dung sai), mỗi shot có mô tả hình và chuyển động.
    /// </summary>
    public static StoryboardContent ParseGrid(string text, int durationSeconds, int gridSize = 3)
    {
        var shotCount = gridSize * gridSize;
        var storyboard = Parse(text, durationSeconds, compactGridShotCount: gridSize == 5 ? shotCount : 0);
        if (storyboard.DurationSeconds != durationSeconds)
            throw new ProjectWorkflowValidationException($"Storyboard {shotCount} ô phải dài đúng {durationSeconds} giây.");
        if (storyboard.Shots.Count != shotCount)
            throw new ProjectWorkflowValidationException($"Storyboard phải có đúng {shotCount} shot, nhận {storyboard.Shots.Count}.");
        if (storyboard.Shots.Any(shot => string.IsNullOrWhiteSpace(shot.ImagePrompt) || string.IsNullOrWhiteSpace(shot.MotionPrompt)))
            throw new ProjectWorkflowValidationException("Mỗi shot của storyboard cần có imagePrompt và motionPrompt.");
        return storyboard;
    }

    public async Task<StoryboardContent> SaveAsync(string projectId, string text, int durationSeconds, CancellationToken cancellationToken = default, int gridSize = 0)
    {
        if (!Guid.TryParse(projectId, out var projectGuid))
            throw new ProjectWorkflowValidationException("Project không hợp lệ.");
        projectId = projectGuid.ToString("D").ToLowerInvariant();
        var storyboard = gridSize > 0 ? ParseGrid(text, durationSeconds, gridSize) : Parse(text, durationSeconds);
        await using var db = new FrayloDbContext(_dbOptions);
        if (!await db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken).ConfigureAwait(false))
            throw new ProjectWorkflowValidationException("Không tìm thấy Project.");

        var imageIds = storyboard.Shots.Where(shot => !string.IsNullOrWhiteSpace(shot.ImageAssetId))
            .Select(shot => shot.ImageAssetId!).Distinct(StringComparer.Ordinal).ToArray();
        var validImages = await db.Assets.CountAsync(asset => imageIds.Contains(asset.Id) &&
            asset.AssetType == "Image" && asset.Status == "Linked" &&
            (asset.ProjectId == null || asset.ProjectId == projectId), cancellationToken).ConfigureAwait(false);
        if (validImages != imageIds.Length)
            throw new ProjectWorkflowValidationException("Ảnh phân cảnh không tồn tại hoặc không thuộc Project.");

        var key = $"project.storyboard.{projectId}";
        var setting = await db.AppSettings.FirstOrDefaultAsync(item => item.Key == key, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("o");
        if (setting == null)
            db.AppSettings.Add(new AppSetting { Key = key, Value = JsonSerializer.Serialize(storyboard), UpdatedAtUtc = now });
        else
        {
            setting.Value = JsonSerializer.Serialize(storyboard);
            setting.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return storyboard;
    }
}
