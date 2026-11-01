namespace Fraylo.Browser.Abstractions;

/// <summary>
/// Đại diện cho một tab context đang được cấp phát cho tác vụ sinh AI cụ thể.
/// </summary>
public interface IBrowserTabHandle : IAsyncDisposable
{
    string ProfileId { get; }
    string Role { get; }
    string JobId { get; }
    bool IsSideEffectSubmitted { get; set; }
    DateTime AcquiredAtUtc { get; }
}

/// <summary>
/// Quản lý Tab Pool cho profile trình duyệt.
/// Với Shared AI: Giới hạn tối đa 3 role tabs ("Script", "Image", "VideoPrompt").
/// Mỗi tab tách biệt hoàn toàn hội thoại nghiệp vụ và tối đa 1 active job/tab.
/// </summary>
public interface ITabPool
{
    /// <summary>
    /// Cấp phát độc quyền một role tab cho tác vụ jobId.
    /// </summary>
    Task<IBrowserTabHandle> AcquireTabAsync(
        string profileId,
        string role,
        string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách tên các role tab đang hoạt động của profile.
    /// </summary>
    IReadOnlyList<string> GetActiveTabs(string profileId);

    /// <summary>
    /// Đếm tổng số job đang chạy trên các tab của profile.
    /// </summary>
    int GetActiveJobCount(string profileId);

    /// <summary>
    /// Kiểm tra xem một role tab có đang bận xử lý job nào hay không.
    /// </summary>
    bool IsRoleActive(string profileId, string role);

    /// <summary>
    /// Lấy toàn bộ danh sách các tab handles đang hoạt động (phục vụ quiescing/drain).
    /// </summary>
    IReadOnlyList<IBrowserTabHandle> GetAllActiveTabs(string profileId);

    /// <summary>
    /// Giải phóng quyền sở hữu role tab sau khi job hoàn thành hoặc bị hủy.
    /// </summary>
    Task ReleaseTabAsync(
        string profileId,
        string role,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Giải phóng quyền sở hữu role tab với kiểm tra chính xác owner jobId.
    /// Tránh trường hợp worker khác xóa nhầm lease của tab đang hoạt động.
    /// </summary>
    Task ReleaseTabAsync(
        string profileId,
        string role,
        string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xóa sạch context nghiệp vụ (cookies/history/chat session) của role tab trước khi tái sử dụng.
    /// </summary>
    Task ResetTabContextAsync(
        string profileId,
        string role,
        CancellationToken cancellationToken = default);
}
