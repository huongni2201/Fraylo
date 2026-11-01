using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Analytics;

public sealed record TikTokAnalyticsAccountDto(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("accountIdentity")] string AccountIdentity,
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("sessionStatus")] string SessionStatus
);

public sealed record TikTokAnalyticsAccountsResponseData(
    [property: JsonPropertyName("accounts")] IReadOnlyList<TikTokAnalyticsAccountDto> Accounts
);

/// <summary>
/// Báo cáo tổng hợp số liệu Analytics theo TikTok account và kỳ thời gian.
/// Tuân thủ nguyên tắc: Không tự sinh số liệu giả, khuyết thì giữ null.
/// </summary>
public sealed record AnalyticsSummaryDto(
    [property: JsonPropertyName("periodDays")] int? PeriodDays,
    [property: JsonPropertyName("startDate")] DateTime? StartDate,
    [property: JsonPropertyName("endDate")] DateTime? EndDate,
    [property: JsonPropertyName("totalVideos")] int TotalVideos,
    [property: JsonPropertyName("totalViews")] long? TotalViews,
    [property: JsonPropertyName("totalLikes")] long? TotalLikes,
    [property: JsonPropertyName("totalComments")] long? TotalComments,
    [property: JsonPropertyName("totalShares")] long? TotalShares,
    [property: JsonPropertyName("averageEngagementRate")] double? AverageEngagementRate,
    [property: JsonPropertyName("totalGmvAmount")] decimal? TotalGmvAmount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("totalAffiliateClicks")] long? TotalAffiliateClicks,
    [property: JsonPropertyName("lastCapturedAtUtc")] DateTime? LastCapturedAtUtc,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("totalProfileViews")] long? TotalProfileViews = null
);

/// <summary>
/// Điểm dữ liệu chuỗi thời gian cho biểu đồ biến thiên (Timeseries chart).
/// </summary>
public sealed record AnalyticsTimeseriesPointDto(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("value")] double? Value
);

/// <summary>
/// Bản ghi chi tiết chỉ số video trong kỳ snapshot phục vụ bảng danh sách Top Videos.
/// </summary>
public sealed record VideoMetricRecordDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("snapshotId")] string SnapshotId,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("videoKey")] string VideoKey,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("views")] long? Views,
    [property: JsonPropertyName("likes")] long? Likes,
    [property: JsonPropertyName("comments")] long? Comments,
    [property: JsonPropertyName("shares")] long? Shares,
    [property: JsonPropertyName("watchTimeAvgSec")] double? WatchTimeAvgSec,
    [property: JsonPropertyName("gmvAmount")] decimal? GmvAmount,
    [property: JsonPropertyName("affiliateClicks")] long? AffiliateClicks,
    [property: JsonPropertyName("publishedAtUtc")] DateTime? PublishedAtUtc,
    [property: JsonPropertyName("capturedAtUtc")] DateTime CapturedAtUtc
);

/// <summary>
/// Kết quả sau khi nhập khẩu (Import) dữ liệu phân tích từ CSV hoặc JSON.
/// </summary>
public sealed record AnalyticsImportResultDto(
    [property: JsonPropertyName("snapshotId")] string SnapshotId,
    [property: JsonPropertyName("recordCount")] int RecordCount,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("periodStart")] DateTime? PeriodStart,
    [property: JsonPropertyName("periodEnd")] DateTime? PeriodEnd,
    [property: JsonPropertyName("capturedAtUtc")] DateTime CapturedAtUtc
);

/// <summary>
/// Yêu cầu truy vấn tổng hợp Analytics.
/// </summary>
public sealed record GetAnalyticsSummaryRequest(
    [property: JsonPropertyName("periodDays")] int? PeriodDays = null,
    [property: JsonPropertyName("startDate")] DateTime? StartDate = null,
    [property: JsonPropertyName("endDate")] DateTime? EndDate = null,
    [property: JsonPropertyName("accountId")] string? AccountId = null
);

/// <summary>
/// Yêu cầu truy vấn chuỗi thời gian theo chỉ số ("views", "likes", "comments", "shares", "gmv", "clicks").
/// </summary>
public sealed record GetAnalyticsTimeseriesRequest(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("periodDays")] int? PeriodDays = null,
    [property: JsonPropertyName("dataType")] string? DataType = null,
    [property: JsonPropertyName("accountId")] string? AccountId = null
);

/// <summary>
/// Yêu cầu lấy danh sách video hàng đầu theo chỉ số sắp xếp.
/// </summary>
public sealed record ListTopVideosRequest(
    [property: JsonPropertyName("sortBy")] string? SortBy = null,
    [property: JsonPropertyName("limit")] int? Limit = null,
    [property: JsonPropertyName("accountId")] string? AccountId = null
);

/// <summary>
/// Yêu cầu nhập khẩu dữ liệu phân tích dạng thô (CSV hoặc JSON).
/// </summary>
public sealed record ImportAnalyticsDataRequest(
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("rawContent")] string RawContent,
    [property: JsonPropertyName("source")] string? Source = null
);

/// <summary>
/// Yêu cầu xuất dữ liệu phân tích thành file tải về.
/// </summary>
public sealed record ExportAnalyticsDataRequest(
    [property: JsonPropertyName("format")] string? Format = null,
    [property: JsonPropertyName("periodDays")] int? PeriodDays = null,
    [property: JsonPropertyName("accountId")] string? AccountId = null
);

/// <summary>
/// Kết quả xuất dữ liệu phân tích.
/// </summary>
public sealed record AnalyticsExportResultDto(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("fileName")] string FileName
);

/// <summary>
/// Yêu cầu kiểm tra trạng thái phiên làm việc (Session/Cookies) của kênh TikTok trong Không gian làm việc.
/// </summary>
public sealed record TikTokCheckSessionRequest(
    [property: JsonPropertyName("profileId")] string? ProfileId = null,
    [property: JsonPropertyName("accountId")] string? AccountId = null
);

/// <summary>
/// Kết quả kiểm tra trạng thái phiên TikTok và danh tính kênh liên kết.
/// </summary>
public sealed record TikTokCheckSessionResponseDto(
    [property: JsonPropertyName("isLoggedIn")] bool IsLoggedIn,
    [property: JsonPropertyName("profileId")] string? ProfileId,
    [property: JsonPropertyName("accountIdentity")] string? AccountIdentity,
    [property: JsonPropertyName("sessionStatus")] string SessionStatus,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage
);

/// <summary>
/// Yêu cầu mở cửa sổ trình duyệt Chromium hiển thị (Headless = false) để người dùng thực hiện đăng nhập kênh TikTok.
/// </summary>
public sealed record TikTokOpenLoginRequest(
    [property: JsonPropertyName("profileId")] string? ProfileId = null,
    [property: JsonPropertyName("targetUrl")] string? TargetUrl = null,
    [property: JsonPropertyName("accountId")] string? AccountId = null
);

/// <summary>
/// Kết quả điều phối mở cửa sổ trình duyệt đăng nhập TikTok.
/// </summary>
public sealed record TikTokOpenLoginResponseDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("visibilityState")] string VisibilityState,
    [property: JsonPropertyName("message")] string Message
);

/// <summary>
/// Yêu cầu đồng bộ dữ liệu chỉ số kênh và danh sách video TikTok trực tiếp qua trình duyệt.
/// </summary>
public sealed record TikTokSyncChannelRequest(
    [property: JsonPropertyName("profileId")] string? ProfileId = null,
    [property: JsonPropertyName("forceOpenBrowser")] bool ForceOpenBrowser = false,
    [property: JsonPropertyName("accountId")] string? AccountId = null
);

/// <summary>
/// Kết quả đồng bộ dữ liệu TikTok Live Crawler vào kho lưu trữ SQLite.
/// </summary>
public sealed record TikTokSyncChannelResponseDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("snapshotId")] string? SnapshotId,
    [property: JsonPropertyName("recordCount")] int RecordCount,
    [property: JsonPropertyName("channelHandle")] string? ChannelHandle,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("requiresLogin")] bool RequiresLogin = false,
    [property: JsonPropertyName("profileId")] string? ProfileId = null,
    [property: JsonPropertyName("overviewRecordCount")] int OverviewRecordCount = 0,
    [property: JsonPropertyName("contentRecordCount")] int ContentRecordCount = 0,
    [property: JsonPropertyName("viewersRecordCount")] int ViewersRecordCount = 0
);

/// <summary>Ba bộ CSV TikTok Studio được nhập thành một snapshot duy nhất.</summary>
public sealed record ImportTikTokStudioExportsRequest(
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("overviewCsv")] string OverviewCsv,
    [property: JsonPropertyName("contentCsv")] string ContentCsv,
    [property: JsonPropertyName("viewersCsv")] string ViewersCsv
);

public sealed record TikTokStudioImportResultDto(
    [property: JsonPropertyName("snapshotId")] string SnapshotId,
    [property: JsonPropertyName("overviewRecordCount")] int OverviewRecordCount,
    [property: JsonPropertyName("contentRecordCount")] int ContentRecordCount,
    [property: JsonPropertyName("viewersRecordCount")] int ViewersRecordCount,
    [property: JsonPropertyName("periodStart")] DateTime? PeriodStart,
    [property: JsonPropertyName("periodEnd")] DateTime? PeriodEnd,
    [property: JsonPropertyName("capturedAtUtc")] DateTime CapturedAtUtc
);

