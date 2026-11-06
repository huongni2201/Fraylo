namespace Fraylo.Infrastructure.Persistence.Entities;

public sealed class FacebookPostBatch
{
    public string Id { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string CreatedAtUtc { get; set; } = string.Empty;
}
