using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Contracts.Analytics;

namespace Fraylo.Application.Analytics;

/// <summary>
/// Giao diện dịch vụ nghiệp vụ phân tích số liệu TikTok & Dashboard Affiliate.
/// Hỗ trợ tổng hợp, trực quan hóa timeseries, xếp hạng top video, nhập khẩu và xuất khẩu dữ liệu.
/// </summary>
public interface IAnalyticsService
{
    Task<IReadOnlyList<TikTokAnalyticsAccountDto>> ListTikTokAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy dữ liệu tổng hợp theo TikTok account và kỳ thời gian.
    /// </summary>
    Task<AnalyticsSummaryDto> GetSummaryAsync(GetAnalyticsSummaryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy chuỗi điểm dữ liệu thời gian (Timeseries) phục vụ vẽ đồ thị biến thiên theo chỉ số.
    /// </summary>
    Task<IReadOnlyList<AnalyticsTimeseriesPointDto>> GetTimeseriesAsync(GetAnalyticsTimeseriesRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách video hàng đầu (Top Videos) theo tiêu chí sắp xếp và số lượng giới hạn.
    /// </summary>
    Task<IReadOnlyList<VideoMetricRecordDto>> ListTopVideosAsync(ListTopVideosRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nhập khẩu dữ liệu phân tích thô (CSV hoặc JSON) tạo ra snapshot bất biến mới.
    /// </summary>
    Task<AnalyticsImportResultDto> ImportDataAsync(ImportAnalyticsDataRequest request, CancellationToken cancellationToken = default);

    Task<TikTokStudioImportResultDto> ImportTikTokStudioExportsAsync(
        ImportTikTokStudioExportsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xuất dữ liệu phân tích của TikTok account ra định dạng CSV (UTF-8 có BOM).
    /// </summary>
    Task<AnalyticsExportResultDto> ExportDataAsync(ExportAnalyticsDataRequest request, CancellationToken cancellationToken = default);
}
