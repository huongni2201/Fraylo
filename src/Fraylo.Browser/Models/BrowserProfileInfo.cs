using Fraylo.Contracts.Browser;

namespace Fraylo.Browser.Models;

/// <summary>
/// Đại diện cho thông tin hồ sơ trình duyệt tại tầng runtime của Host Fraylo.
/// Bảo mật: Đường dẫn vật lý <see cref="UserDataDir"/> chỉ lưu hành nội bộ, tuyệt đối không trả về Web UI.
/// </summary>
public sealed class BrowserProfileInfo
{
    public string ProfileId { get; set; } = string.Empty;
    public string Kind { get; set; } = BrowserProfileKind.SharedAI;
    public string? AccountIdentity { get; set; }
    public string? ProviderKey { get; set; }
    public string? DisplayName { get; set; }
    public string Lifecycle { get; set; } = "Active";
    public string UserDataDir { get; set; } = string.Empty;
    public string VisibilityState { get; set; } = BrowserVisibilityState.Hidden;
    public string SessionStatus { get; set; } = BrowserSessionStatus.Unverified;
    public string OperationalState { get; set; } = BrowserOperationalState.Stopped;
    public int ActiveJobCount { get; set; }
    public List<string> ActiveTabs { get; set; } = [];

    public BrowserProfileDto ToDto() =>
        new(
            ProfileId: ProfileId,
            Kind: Kind,
            AccountIdentity: AccountIdentity,
            VisibilityState: VisibilityState,
            SessionStatus: SessionStatus,
            OperationalState: OperationalState,
            ActiveJobCount: ActiveJobCount,
            ActiveTabs: ActiveTabs.AsReadOnly(),
            ProviderKey: ProviderKey,
            DisplayName: DisplayName,
            Lifecycle: Lifecycle);
}
