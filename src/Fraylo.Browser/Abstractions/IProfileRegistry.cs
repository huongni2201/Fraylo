using Fraylo.Browser.Models;
using Fraylo.Contracts.Browser;

namespace Fraylo.Browser.Abstractions;

/// <summary>
/// Quản lý danh mục profile trình duyệt, ánh xạ đường dẫn vật lý an toàn và lưu bền vững trạng thái trong SQLite.
/// </summary>
public interface IProfileRegistry
{
    /// <summary>
    /// Lấy thông tin profile trình duyệt theo ID.
    /// </summary>
    Task<BrowserProfileInfo?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Liệt kê toàn bộ profile cấp ứng dụng.
    /// </summary>
    Task<IReadOnlyList<BrowserProfileInfo>> ListProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Đảm bảo profile tồn tại trong cơ sở dữ liệu SQLite; nếu chưa có thì tạo mới theo quy chuẩn.
    /// </summary>
    Task<BrowserProfileInfo> EnsureProfileAsync(
        string profileId,
        string kind,
        string? accountIdentity = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cập nhật trạng thái hiển thị, session, vận hành hoặc danh tính tài khoản của profile và lưu SQLite.
    /// </summary>
    Task UpdateProfileStateAsync(
        string profileId,
        string? visibilityState = null,
        string? sessionStatus = null,
        string? operationalState = null,
        string? accountIdentity = null,
        string? reasonCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tính toán đường dẫn thư mục userDataDir an toàn nằm dưới %LOCALAPPDATA%/TiToAI/profiles/{profileId}.
    /// Tuyệt đối không chứa email, username hay ký tự directory traversal.
    /// </summary>
    string GetUserDataDir(string profileId, string kind);

    /// <summary>
    /// Phục hồi sau sự cố khi ứng dụng khởi động lại: Reconcile các checkpoint chuyển giao chưa kết thúc,
    /// đưa trạng thái về Stopped/Hidden và không phục hồi Connected nếu chưa thẩm định lại.
    /// </summary>
    Task ReconcileCheckpointsAtStartupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sự kiện phát ra khi trạng thái của profile thay đổi.
    /// </summary>
    event Func<BrowserProfileStateChangedPayload, Task>? ProfileStateChanged;
}
