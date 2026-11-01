namespace Fraylo.Infrastructure.Persistence.Entities;

public class BrowserProfile
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty; // SharedAI | MuseAI | TikTok
    public string? AccountIdentity { get; set; }
    public string VisibilityState { get; set; } = "Hidden";
    public string SessionStatus { get; set; } = "Unverified";
    public string OperationalState { get; set; } = "Stopped";
    public string? ProviderKey { get; set; } // chatgpt-web | gemini-web | museai | tiktok
    public string? DisplayName { get; set; }
    public string Lifecycle { get; set; } = "Active"; // Active | ArchivedLegacy
    public long Revision { get; set; } = 1;
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;

}
