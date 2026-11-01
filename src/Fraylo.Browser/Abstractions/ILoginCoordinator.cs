using Fraylo.Contracts.Browser;

namespace Fraylo.Browser.Abstractions;

/// <summary>
/// Điều phối đăng nhập trình duyệt hiển thị (Headless = false) trên desktop Windows.
/// Cho phép người dùng chủ động mở đồng thời nhiều cửa sổ đăng nhập cho các profile độc lập,
/// duy trì single-instance lock theo từng profile (userDataDir) và tuân thủ ngân sách tài nguyên max_open_browsers.
/// </summary>
public interface ILoginCoordinator
{
    /// <summary>
    /// ID của profile hiện đang nắm giữ cửa sổ đăng nhập hiển thị (hoặc profile đầu tiên nếu có nhiều cửa sổ, null nếu không có).
    /// </summary>
    string? CurrentVisibleProfileId { get; }

    /// <summary>
    /// Danh sách toàn bộ profile ID hiện đang có cửa sổ đăng nhập hiển thị trên desktop.
    /// </summary>
    IReadOnlyCollection<string> ActiveVisibleProfileIds { get; }

    /// <summary>
    /// Cho biết hiện có ít nhất một cửa sổ đăng nhập nào đang hiển thị trên desktop hay không.
    /// </summary>
    bool IsVisibleLoginActive { get; }

    /// <summary>
    /// Kiểm tra xem một profileId cụ thể hiện có đang mở cửa sổ đăng nhập hiển thị hay không.
    /// </summary>
    bool IsProfileVisible(string profileId);

    /// <summary>
    /// Yêu cầu mở cửa sổ đăng nhập hiển thị cho profileId.
    /// Nếu profile đã mở, không mở trùng cửa sổ mới.
    /// </summary>
    Task<BrowserRequestLoginResponseData> RequestLoginAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Yêu cầu mở cửa sổ đăng nhập hiển thị cho profileId và tự động điều hướng tới URL của Provider (ChatGPT, Gemini, MuseAI, v.v.).
    /// </summary>
    Task<BrowserRequestLoginResponseData> RequestLoginAsync(
        string profileId,
        string? targetUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hủy phiên đăng nhập hoặc đóng cửa sổ đăng nhập của profileId.
    /// </summary>
    Task<BrowserCancelLoginResponseData> CancelLoginAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đóng cửa sổ đăng nhập hiển thị của profileId, giải phóng slot của profile và phát sự kiện trạng thái kèm mã lý do.
    /// Tuyệt đối không tự ý mở lại cửa sổ sau khi người dùng đóng.
    /// </summary>
    Task CloseLoginWindowAsync(
        string profileId,
        string reasonCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Chờ đợi cho đến khi profileId nhận được slot hiển thị hoặc timeout.
    /// </summary>
    Task<bool> WaitForVisibleSlotAsync(
        string profileId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Giải phóng quyền sở hữu slot đăng nhập hiển thị của profileId.
    /// </summary>
    void ReleaseVisibleSlot(string profileId);
}
