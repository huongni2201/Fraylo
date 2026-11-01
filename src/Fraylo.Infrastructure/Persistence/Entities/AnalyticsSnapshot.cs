using System;
using System.Collections.Generic;

namespace Fraylo.Infrastructure.Persistence.Entities;

/// <summary>
/// Snapshot bất biến của dữ liệu Analytics theo TikTok account.
/// Lưu trữ bằng chứng và phân cách rõ ràng từng lần thu thập/nhập khẩu dữ liệu.
/// </summary>
public class AnalyticsSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public int RecordCount { get; set; }
    public string? RawManifest { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public ICollection<VideoAnalyticsRecord> Records { get; set; } = new List<VideoAnalyticsRecord>();
    public ICollection<TikTokAnalyticsDailyRecord> DailyRecords { get; set; } = new List<TikTokAnalyticsDailyRecord>();
}
