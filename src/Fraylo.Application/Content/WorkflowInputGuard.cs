using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Application.Content;

/// <summary>
/// Quy tắc chung khi đầu vào video đổi ngoài ProjectWorkflowService (form Cảnh, Nhóm, đổi nhóm của Dự án):
/// không đổi giữa run đang chạy, và video đã render được đánh dấu stale để Đăng video chặn nó.
/// </summary>
internal static class WorkflowInputGuard
{
    public static async Task<bool> HasActiveRunAsync(FrayloDbContext dbContext, IReadOnlyCollection<string> projectIds, CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0) return false;
        var keys = projectIds.Select(id => "project.workflow.config." + id).ToList();
        var configs = await dbContext.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .Select(s => s.Value)
            .ToListAsync(cancellationToken);
        return configs.Any(value => ReadRunState(value) is WorkflowRunState.Running or WorkflowRunState.Paused);
    }

    public static async Task<bool> IsRunRunningAsync(FrayloDbContext dbContext, string projectId, CancellationToken cancellationToken)
    {
        var config = await dbContext.AppSettings.AsNoTracking()
            .Where(s => s.Key == "project.workflow.config." + projectId)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return ReadRunState(config) == WorkflowRunState.Running;
    }

    public static Task<bool> HasActiveSceneJobAsync(FrayloDbContext dbContext, string sceneId, CancellationToken cancellationToken) =>
        dbContext.Jobs.AsNoTracking().AnyAsync(j => j.SceneId == sceneId && JobState.ActiveStates.Contains(j.State), cancellationToken);

    /// <summary>Đánh dấu stale video đã render của các Dự án (cần tạo lại prompt video và video).</summary>
    public static async Task MarkProjectVideosStaleAsync(FrayloDbContext dbContext, IReadOnlyCollection<string> projectIds, string reason, CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0) return;
        var sceneIds = await dbContext.Scenes.AsNoTracking()
            .Where(s => projectIds.Contains(s.ProjectId) && s.VideoAssetId != null)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        foreach (var sceneId in sceneIds)
        {
            await MarkSceneVideoStaleAsync(dbContext, sceneId, reason, [JobTaskType.VideoPrompt, JobTaskType.VideoRender], cancellationToken);
        }
    }

    /// <summary>Ghi cờ stale vào metadata workflow của cảnh, giữ các trường khác. Cảnh chưa có metadata workflow thì bỏ qua.</summary>
    public static async Task MarkSceneVideoStaleAsync(FrayloDbContext dbContext, string sceneId, string reason, IEnumerable<string> staleTaskTypes, CancellationToken cancellationToken)
    {
        var meta = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == "scene.workflow." + sceneId, cancellationToken);
        if (meta == null || string.IsNullOrWhiteSpace(meta.Value) || JsonNode.Parse(meta.Value) is not JsonObject node)
        {
            return;
        }

        var types = (node["staleTaskTypes"] as JsonArray)?.Select(t => t?.GetValue<string>()).OfType<string>().ToHashSet()
            ?? new HashSet<string>();
        types.UnionWith(staleTaskTypes);
        node["isStale"] = true;
        node["workflowStatus"] = SceneWorkflowStatus.NeedsReview;
        node["staleReason"] = reason;
        node["staleTaskTypes"] = new JsonArray(types.Select(t => (JsonNode?)t).ToArray());
        meta.Value = node.ToJsonString();
        meta.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o");
    }

    private static string? ReadRunState(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        using var document = JsonDocument.Parse(configJson);
        var runState = document.RootElement.EnumerateObject()
            .FirstOrDefault(p => p.Name.Equals("runState", StringComparison.OrdinalIgnoreCase)).Value;
        return runState.ValueKind == JsonValueKind.String ? runState.GetString() : null;
    }
}
