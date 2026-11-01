namespace Fraylo.Browser.Abstractions;

/// <summary>
/// Đại diện cho khóa sở hữu độc quyền của một profile trình duyệt.
/// Khi dispose, khóa sẽ được giải phóng an toàn cả trong tiến trình lẫn cấp hệ điều hành.
/// </summary>
public interface IProfileOwnerLock : IAsyncDisposable, IDisposable
{
    string ProfileId { get; }
    string OwnerTag { get; }
    DateTime AcquiredAtUtc { get; }
}

/// <summary>
/// Quản lý cơ chế Single-Instance Profile Lock cho userDataDir của từng profile trình duyệt.
/// Ngăn chặn đồng thời hai tiến trình hoặc hai ngữ cảnh mở cùng một thư mục Chromium userDataDir.
/// </summary>
public interface IProfileOwner
{
    /// <summary>
    /// Yêu cầu chiếm quyền sở hữu độc quyền đối với profileId.
    /// </summary>
    Task<IProfileOwnerLock> AcquireOwnerLockAsync(
        string profileId,
        string ownerTag,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra xem profileId hiện có đang bị khóa hay không.
    /// </summary>
    bool IsLocked(string profileId);

    /// <summary>
    /// Lấy tag định danh của owner hiện đang nắm giữ khóa profile.
    /// </summary>
    string? GetCurrentOwnerTag(string profileId);
}
