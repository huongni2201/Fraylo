namespace Fraylo.Infrastructure.Persistence.Entities;

public sealed class FacebookPostBatch
{
    public bool RequiresRewrite { get; set; }
    public string? OriginalTextSnapshot { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Paused { get; set; } = false;
    public long Revision { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string CreatedAtUtc { get; set; } = string.Empty;
}
