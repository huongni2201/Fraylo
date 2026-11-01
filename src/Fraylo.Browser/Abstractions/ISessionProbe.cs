using Fraylo.Contracts.Browser;

namespace Fraylo.Browser.Abstractions;

/// <summary>
/// Thẩm định trạng thái session cookies và tính hợp lệ của tài khoản trong profile trình duyệt.
/// </summary>
public interface ISessionProbe
{
    /// <summary>
    /// Kiểm tra session hiện thời của profileId và đối soát danh tính tài khoản nếu có cấu hình.
    /// </summary>
    Task<BrowserCheckSessionResponseData> CheckSessionAsync(
        string profileId,
        CancellationToken cancellationToken = default);
}
