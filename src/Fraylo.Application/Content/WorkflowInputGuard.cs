using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Content;

/// <summary>
/// Quy tắc chung khi đầu vào video đổi ngoài ProjectWorkflowService (form Cảnh):
/// không đổi giữa run đang chạy, và video đã render được đánh dấu stale để Đăng video chặn nó.
/// </summary>
internal static class WorkflowInputGuard
{

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

    /// <summary>Ghi cờ stale vào metadata workflow của cảnh, giữ các trường khác. Cảnh chưa có metadata thì tạo mới để Đăng video vẫn chặn được.</summary>
    public static async Task MarkSceneVideoStaleAsync(FrayloDbContext dbContext, string sceneId, string reason, IEnumerable<string> staleTaskTypes, CancellationToken cancellationToken)
    {
        var key = "scene.workflow." + sceneId;
        var meta = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (meta == null)
        {
            meta = new AppSetting { Key = key, Value = "{}", UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o") };
            dbContext.AppSettings.Add(meta);
        }
        var node = (string.IsNullOrWhiteSpace(meta.Value) ? null : JsonNode.Parse(meta.Value)) as JsonObject ?? new JsonObject();
        node["sceneId"] ??= sceneId;

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
