namespace Fraylo.Browser.Abstractions;

/// <summary>
/// Điều phối quy trình Quiescing 4 bước và chuyển giao (handoff) quyền sở hữu sang luồng đăng nhập hiển thị.
/// Bảo đảm dừng an toàn các tab chưa submit (cập nhật về Queued, xóa lease) và chờ các tab đã submit hoàn tất.
/// </summary>
public interface IBrowserPauseDrainController
{
    /// <summary>
    /// Kiểm tra xem profileId hiện có đang bị tạm dừng phân phối tác vụ mới hay không.
    /// </summary>
    bool IsDispatchPaused(string profileId);

    /// <summary>
    /// Bước 1: Kích hoạt cờ tạm dừng phân phối job mới vào toàn bộ các tab của profileId.
    /// </summary>
    Task PauseDispatchAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bước 2: Dừng an toàn các tab chưa submit side-effect (side_effect_submitted == 0),
    /// gọi callback onTabDrain để worker chuyển Job về Queued, xóa lease, không tăng attempts.
    /// </summary>
    Task DrainPendingTabsAsync(
        string profileId,
        Func<string, Task>? onTabDrain = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bước 3: Đợi các tab đã submit side-effect hoàn tất hoặc đến hạn timeout.
    /// </summary>
    Task WaitForInFlightTabsAsync(
        string profileId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bước 4: Lưu checkpoint chuyển giao vào SQLite, đóng context chạy ẩn và chuẩn bị handoff sang cửa sổ hiển thị.
    /// </summary>
    Task PrepareHandoffToVisibleLoginAsync(
        string profileId,
        string? activeJobId,
        string reasonCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hoàn tất luồng handoff sau khi người dùng kết thúc đăng nhập:
    /// Đóng cửa sổ hiển thị, giải phóng Semaphore, đánh dấu checkpoint resolved và chuẩn bị resume context ẩn.
    /// </summary>
    Task CompleteLoginHandoffAsync(
        string profileId,
        bool authSuccess,
        CancellationToken cancellationToken = default);
}
