namespace Fraylo.Infrastructure.Persistence.Entities;

/// <summary>
/// Ràng buộc thực thi bất biến giữa Job và ProviderAccount/Profile/TabSlot khi được Reserve đầu tiên
/// theo execution-contract-v1.1 §3.1 và §4.1.
/// </summary>
public class ExecutionBinding
{
    public string JobId { get; set; } = string.Empty;
    public string ProviderAccountId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string? Identity { get; set; }
    public long CapacityConfigRevision { get; set; }
    public string TabSlotId { get; set; } = string.Empty;
    public string AssignedAtUtc { get; set; } = string.Empty;
    public string? LeaseOwnerId { get; set; }
    public long LeaseToken { get; set; }

    public Job? Job { get; set; }
    public ProviderAccount? ProviderAccount { get; set; }
    public BrowserProfile? Profile { get; set; }
}
