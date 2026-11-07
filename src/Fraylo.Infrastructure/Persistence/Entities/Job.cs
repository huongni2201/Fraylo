namespace Fraylo.Infrastructure.Persistence.Entities;

public class Job
{
    public string Id { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string? AnalyticsReportId { get; set; }
    public string? FacebookRewriteChunkId { get; set; }
    public FacebookRewriteChunk? FacebookRewriteChunk { get; set; }
    public AnalyticsAnalysisReport? AnalyticsReport { get; set; }
    public string? SceneId { get; set; }
    public string TaskType { get; set; } = string.Empty; // Script | Image | VideoPrompt | VideoRender
    public string State { get; set; } = "Queued"; // Queued | Reserved | Running | AwaitingUser | Succeeded | Failed | Canceled
    public string? ReasonCode { get; set; }
    public string ProviderKey { get; set; } = string.Empty; // gemini-web | chatgpt-web | museai
    public string? ConfigSnapshotJson { get; set; }
    public string? PromptSnapshotText { get; set; }
    public string StorageRootId { get; set; } = string.Empty;
    public int Attempts { get; set; } = 0;
    public int MaxAttempts { get; set; } = 3;
    public string? LeaseOwnerId { get; set; }
    public long FencingToken { get; set; } = 0;
    public string? LeaseExpiresAtUtc { get; set; }
    public string? RetryAfterUtc { get; set; }
    public int SideEffectSubmitted { get; set; } = 0;
    public string? SideEffectAtUtc { get; set; }
    public string? ResultReference { get; set; }
    public string? ResultText { get; set; }
    public int CancelRequested { get; set; } = 0;
    public string? LastHeartbeatAtUtc { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;

    public Project? Project { get; set; }
    public Scene? Scene { get; set; }
    public ExecutionBinding? ExecutionBinding { get; set; }
}
