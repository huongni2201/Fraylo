namespace Fraylo.Infrastructure.Persistence.Entities;

public class BrowserHandoffCheckpoint
{
    public string Id { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string? ActiveJobId { get; set; }
    public string PreviousState { get; set; } = string.Empty;
    public string TargetVisibilityState { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string? ResolvedAtUtc { get; set; }

    public BrowserProfile? Profile { get; set; }
}
