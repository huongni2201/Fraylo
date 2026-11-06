namespace Fraylo.Infrastructure.Persistence.Entities;

public sealed class FacebookPostTaskMedia
{
    public string TaskId { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int SortOrder { get; set; } = 0;
}
