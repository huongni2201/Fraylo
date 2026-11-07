namespace Fraylo.Infrastructure.Persistence.Entities;

public sealed class FacebookAnalyticsPage
{
    public string Id { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string FacebookPageId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string VerifiedAtUtc { get; set; } = "";
}

public sealed class FacebookAnalyticsCapture
{
    public string Id { get; set; } = "";
    public string PageId { get; set; } = "";
    public string Status { get; set; } = "Running";
    public string StartedAtUtc { get; set; } = "";
    public string? FinishedAtUtc { get; set; }
    public string ActivityLogJson { get; set; } = "[]";
    public string Message { get; set; } = "";
    public int RecordCount { get; set; }
    public string Source { get; set; } = "meta-business-suite-published-vi-v1";
    public string Scope { get; set; } = "Trọn đời · tối đa 50 nội dung · ngày nguồn theo Giờ Thái Bình Dương";
    public string Owner { get; set; } = "";
}

public sealed class FacebookContentMetricSnapshot
{
    public string Id { get; set; } = "";
    public string CaptureId { get; set; } = "";
    public string ContentId { get; set; } = "";
    public string DataJson { get; set; } = "";
}
