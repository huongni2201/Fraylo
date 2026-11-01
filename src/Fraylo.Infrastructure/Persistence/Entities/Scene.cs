namespace Fraylo.Infrastructure.Persistence.Entities;

public class Scene
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int SequenceOrder { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal TargetDurationSeconds { get; set; }
    public string? PromptText { get; set; }
    public string Status { get; set; } = "Empty"; // Empty | Draft | Queued | Running | Completed | Failed
    public string? AudioAssetId { get; set; }
    public string? ImageAssetId { get; set; }
    public string? VideoAssetId { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;

    public Project? Project { get; set; }
    public Asset? AudioAsset { get; set; }
    public Asset? ImageAsset { get; set; }
    public Asset? VideoAsset { get; set; }
}
