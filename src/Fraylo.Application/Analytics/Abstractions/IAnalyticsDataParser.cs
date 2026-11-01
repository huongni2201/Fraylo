using System.Collections.Generic;
using Fraylo.Application.Analytics;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Analytics.Abstractions;

/// <summary>
/// Giao diện phân tích và xuất dữ liệu chỉ số video mạng xã hội cho từng nền tảng (TikTok, YouTube, Facebook,...).
/// </summary>
public interface IAnalyticsDataParser
{
    /// <summary>
    /// Định danh nền tảng (ví dụ: "tiktok").
    /// </summary>
    string PlatformKey { get; }

    /// <summary>
    /// Phân tích nội dung thô (CSV hoặc JSON) thành batch các bản ghi video metrics.
    /// </summary>
    ParsedAnalyticsBatch ParseRawContent(string rawContent, string format);

    ParsedTikTokStudioExports ParseTikTokStudioExports(
        string overviewCsv,
        string contentCsv,
        string viewersCsv,
        DateTime capturedAtUtc);

    /// <summary>
    /// Xuất danh sách bản ghi phân tích ra định dạng CSV UTF-8 có BOM.
    /// </summary>
    string ExportCsv(IReadOnlyList<VideoAnalyticsRecord> records);
}
