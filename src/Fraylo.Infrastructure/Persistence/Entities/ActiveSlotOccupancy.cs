namespace Fraylo.Infrastructure.Persistence.Entities;

/// <summary>
/// Đại diện cho việc chiếm giữ vị trí tab/slot (Slot Occupancy) đang hoạt động trong DB
/// theo H2 và plan 1.2 §2.2.
/// Ràng buộc cấp SQLite Unique/PK (ProviderAccountId, TabSlotId) ngăn chặn tuyệt đối trùng slot,
/// và Unique(JobId) bảo đảm một job chỉ chiếm tối đa một slot tại một thời điểm.
/// </summary>
public class ActiveSlotOccupancy
{
    public string ProviderAccountId { get; set; } = string.Empty;
    public string TabSlotId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string LeaseOwnerId { get; set; } = string.Empty;
    public long LeaseToken { get; set; }
    public string AcquiredAtUtc { get; set; } = string.Empty;

    public ProviderAccount? ProviderAccount { get; set; }
    public Job? Job { get; set; }
}
