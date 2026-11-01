namespace Fraylo.Infrastructure.Persistence.Entities;

/// <summary>
/// Daily aggregate metrics imported from TikTok Studio's Overview and Viewers CSV exports.
/// Metrics absent from an export remain null.
/// </summary>
public sealed class TikTokAnalyticsDailyRecord
{
    public string Id { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public DateTime PeriodDateUtc { get; set; }
    public long? VideoViews { get; set; }
    public long? ProfileViews { get; set; }
    public long? Likes { get; set; }
    public long? Comments { get; set; }
    public long? Shares { get; set; }
    public long? TotalViewers { get; set; }
    public long? NewViewers { get; set; }
    public long? ReturningViewers { get; set; }
    public DateTime CapturedAtUtc { get; set; }

    public AnalyticsSnapshot? Snapshot { get; set; }
}
