namespace Fraylo.Infrastructure.Persistence.Entities;

public sealed class FacebookPostTask
{
    public string Id { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string AccountIdentitySnapshot { get; set; } = string.Empty;
    public string GroupNameSnapshot { get; set; } = string.Empty;
    public string GroupUrlSnapshot { get; set; } = string.Empty;
    public string TextSnapshot { get; set; } = string.Empty;
    public string State { get; set; } = "Queued";
    public string? ReasonCode { get; set; } = null;
    public string Message { get; set; } = string.Empty;
    public string? SubmittedAtUtc { get; set; } = null;
    public string? LeaseOwnerId { get; set; } = null;
    public string? LeaseExpiresAtUtc { get; set; } = null;
    public bool CancelRequested { get; set; } = false;
    public long Revision { get; set; } = 1;
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;
}
