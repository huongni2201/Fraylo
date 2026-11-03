using Microsoft.Playwright;

namespace Fraylo.Browser.Abstractions;

/// <summary>
/// Đại diện cho một phiên ngữ cảnh trình duyệt Playwright đang chạy dưới quyền sở hữu độc quyền.
/// </summary>
public interface IBrowserContextHandle : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// ID hồ sơ trình duyệt được quản lý.
    /// </summary>
    string ProfileId { get; }

    /// <summary>
    /// Ngữ cảnh trình duyệt Playwright Chromium Persistent Context.
    /// </summary>
    IBrowserContext Context { get; }

    /// <summary>
    /// Khóa sở hữu độc quyền single-instance lock (.Fraylo_lock).
    /// </summary>
    IProfileOwnerLock OwnerLock { get; }

    /// <summary>
    /// Cho biết trình duyệt đang chạy ở chế độ ẩn (headless) hay hiển thị.
    /// </summary>
    bool IsHeadless { get; }
}

/// <summary>
/// Quản lý tiến trình và vòng đời Browser Chromium Persistent Context của Playwright.
/// Tích hợp cơ chế Single-Instance Lock (.Fraylo_lock) để đảm bảo không xung đột dữ liệu profile.
/// </summary>
public interface IBrowserProcessController : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Khởi chạy persistent context của Chromium cho profile được chỉ định.
    /// Bắt buộc chiếm single-instance lock (.Fraylo_lock) trước khi mở trình duyệt.
    /// </summary>
    Task<IBrowserContextHandle> LaunchPersistentContextAsync(
        string profileId,
        bool isHeadless,
        CancellationToken cancellationToken = default);

    /// <summary>Visible automation trial; closing it must not emit a login-close event.</summary>
    Task<IBrowserContextHandle> LaunchVisibleAutomationContextAsync(
        string profileId,
        CancellationToken cancellationToken = default) =>
        LaunchPersistentContextAsync(profileId, isHeadless: false, cancellationToken);

    /// <summary>
    /// Mở cửa sổ đăng nhập thủ công của profile. Bản thật mở Chrome thường trên userDataDir của profile
    /// (không qua Playwright) và phát ContextClosed khi người dùng đóng cửa sổ.
    /// </summary>
    Task LaunchLoginWindowAsync(
        string profileId,
        string? initialUrl,
        CancellationToken cancellationToken = default) =>
        LaunchPersistentContextAsync(profileId, isHeadless: false, initialUrl, cancellationToken);

    /// <summary>
    /// Khởi chạy persistent context của Chromium cho profile được chỉ định và điều hướng tới initialUrl (nếu có).
    /// </summary>
    Task<IBrowserContextHandle> LaunchPersistentContextAsync(
        string profileId,
        bool isHeadless,
        string? initialUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Điều hướng cửa sổ trình duyệt đang hoạt động của profileId tới URL chỉ định.
    /// </summary>
    Task NavigateAsync(
        string profileId,
        string url,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đóng persistent context của profile và giải phóng single-instance lock an toàn.
    /// </summary>
    Task CloseContextAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra xem profileId hiện có đang chạy tiến trình Playwright persistent context hay không.
    /// </summary>
    bool IsRunning(string profileId);

    /// <summary>
    /// Cho biết cửa sổ đăng nhập Chrome thường của profileId có đang mở (đang giữ .Fraylo_lock) hay không.
    /// </summary>
    bool IsLoginWindowOpen(string profileId) => false;

    /// <summary>
    /// Lấy ngữ cảnh trình duyệt Playwright đang hoạt động của profileId (nếu đang chạy).
    /// Trả về null nếu profile không có tiến trình đang hoạt động.
    /// </summary>
    IBrowserContextHandle? TryGetContext(string profileId);

    /// <summary>
    /// Khởi động ngầm runtime automation (Playwright process driver) để tối ưu hóa thời gian mở trình duyệt (loại bỏ cold start).
    /// </summary>
    Task PrewarmAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sự kiện phát ra khi một persistent context của profile bị đóng (bởi người dùng nhấn X, tiến trình kết thúc hoặc đóng thủ công).
    /// </summary>
    event EventHandler<string>? ContextClosed;
}
