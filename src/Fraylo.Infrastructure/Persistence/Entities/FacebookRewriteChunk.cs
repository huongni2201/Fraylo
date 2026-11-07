namespace Fraylo.Infrastructure.Persistence.Entities;

public sealed class FacebookRewriteChunk
{
    public string Id { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string TaskIdsJson { get; set; } = "[]";
    public string? AppliedAtUtc { get; set; }
    public string? ApplyError { get; set; }
    public Job? Job { get; set; }
    public FacebookPostBatch Batch { get; set; } = null!;
}
