using System.Threading;
using System.Threading.Tasks;
using Fraylo.Contracts.Analytics;

namespace Fraylo.Application.Analytics;

/// <summary>
/// Giao diện điều phối thu thập và đồng bộ dữ liệu phân tích kênh TikTok trực tiếp qua trình duyệt Playwright.
/// </summary>
public interface ITikTokAnalyticsCrawler
{
    /// <summary>
    /// Kiểm tra trạng thái phiên đăng nhập TikTok và danh tính kênh hiện hành trong Không gian làm việc.
    /// </summary>
    Task<TikTokCheckSessionResponseDto> CheckSessionAsync(TikTokCheckSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mở cửa sổ trình duyệt Chromium hiển thị (Headless = false) cho người dùng đăng nhập tài khoản TikTok.
    /// </summary>
    Task<TikTokOpenLoginResponseDto> OpenLoginAsync(TikTokOpenLoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tự xuất ba báo cáo Overview, Content, Viewers từ TikTok Studio và lưu snapshot bất biến trong SQLite.
    /// </summary>
    Task<TikTokSyncChannelResponseDto> SyncChannelDataAsync(TikTokSyncChannelRequest request, CancellationToken cancellationToken = default);
}
