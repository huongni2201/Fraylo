using System;

namespace Fraylo.Infrastructure.Persistence.Entities;

/// <summary>
/// Bản ghi chi tiết chỉ số video trong một snapshot phân tích.
/// Bảo toàn tính trung thực của dữ liệu: trường nào thiếu hoặc không có nguồn thì mang giá trị null, không gán 0.
/// </summary>
public class VideoAnalyticsRecord
{
    public string Id { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string VideoKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long? Views { get; set; }
    public long? Likes { get; set; }
    public long? Comments { get; set; }
    public long? Shares { get; set; }
    public double? WatchTimeAvgSec { get; set; }
    public decimal? GmvAmount { get; set; }
    public long? AffiliateClicks { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime CapturedAtUtc { get; set; }

    public AnalyticsSnapshot? Snapshot { get; set; }
}
