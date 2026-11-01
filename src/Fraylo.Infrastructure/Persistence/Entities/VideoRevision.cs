namespace Fraylo.Infrastructure.Persistence.Entities;

public sealed class VideoRevision
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string State { get; set; } = "Draft";
    public string? AssetId { get; set; }
    public string? ContentHash { get; set; }
    public double? DurationSeconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? ProvenanceJson { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;

    public Project? Project { get; set; }
    public Asset? Asset { get; set; }
}
