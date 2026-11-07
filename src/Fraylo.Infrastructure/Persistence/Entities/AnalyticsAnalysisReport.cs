namespace Fraylo.Infrastructure.Persistence.Entities;

public sealed class AnalyticsAnalysisReport
{
    public string Id { get; set; } = "";
    public string Platform { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string? PageId { get; set; }
    public string SourceId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string? ProviderKey { get; set; }
    public long SettingsRevision { get; set; }
    public string? PreparationReason { get; set; }
    public string? InputJson { get; set; }
    public string? InputHash { get; set; }
    public Job? Job { get; set; }
}
