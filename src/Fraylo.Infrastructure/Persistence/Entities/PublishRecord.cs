namespace Fraylo.Infrastructure.Persistence.Entities;

public sealed class PublishRecord
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string VideoRevisionId { get; set; } = string.Empty;
    public string? AccountId { get; set; }
    public string CaptionSnapshot { get; set; } = string.Empty;
    public string? ExternalPostUrl { get; set; }
    public string? ExternalPostId { get; set; }
    public string State { get; set; } = "Prepared";
    public string? UserEnteredAtUtc { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;

    public Project? Project { get; set; }
    public VideoRevision? VideoRevision { get; set; }
}
