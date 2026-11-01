namespace Fraylo.Infrastructure.Persistence.Entities;

/// <summary>
/// Tài khoản AI cấp toàn ứng dụng (ChatGPT, Gemini, MuseAI) theo execution-contract-v1.1 §1.
/// Dùng chung toàn ứng dụng, không gắn cố định vào một Project hoặc TikTok account.
/// </summary>
public class ProviderAccount
{
    public string Id { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty; // chatgpt-web | gemini-web | museai
    public string ProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? VerifiedIdentity { get; set; }
    public string? IdentityVerifiedAtUtc { get; set; }
    public bool Enabled { get; set; } = false;
    public string Lifecycle { get; set; } = "Active"; // Active | ArchivedLegacy
    public int MaxConcurrentJobs { get; set; } = 3;
    public int MaxTabs { get; set; } = 3;
    public string? PerTaskLimitsJson { get; set; }
    public long Revision { get; set; } = 1;
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;

    public BrowserProfile? Profile { get; set; }
}
