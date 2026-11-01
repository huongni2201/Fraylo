namespace Fraylo.Browser.Models;

/// <summary>
/// Các phân loại hồ sơ trình duyệt được hỗ trợ bởi Fraylo.
/// </summary>
public static class BrowserProfileKind
{
    public const string SharedAI = "SharedAI";
    public const string MuseAI = "MuseAI";
    public const string TikTok = "TikTok";

    public static bool IsValid(string kind) =>
        kind is SharedAI or MuseAI or TikTok;
}

/// <summary>
/// Trạng thái hiển thị của cửa sổ trình duyệt trên desktop.
/// </summary>
public static class BrowserVisibilityState
{
    public const string Hidden = "Hidden";
    public const string VisibleWaiting = "VisibleWaiting";
    public const string VisibleActive = "VisibleActive";

    public static bool IsValid(string state) =>
        state is Hidden or VisibleWaiting or VisibleActive;
}

/// <summary>
/// Trạng thái phiên làm việc (session/cookies) của hồ sơ trình duyệt.
/// </summary>
public static class BrowserSessionStatus
{
    public const string Connected = "Connected";
    public const string SessionExpired = "SessionExpired";
    public const string Unverified = "Unverified";

    public static bool IsValid(string status) =>
        status is Connected or SessionExpired or Unverified;
}

/// <summary>
/// Trạng thái vận hành tiến trình/tác vụ của hồ sơ trình duyệt.
/// </summary>
public static class BrowserOperationalState
{
    public const string Stopped = "Stopped";
    public const string Idle = "Idle";
    public const string Running = "Running";
    public const string Quiescing = "Quiescing";
    public const string Error = "Error";

    public static bool IsValid(string state) =>
        state is Stopped or Idle or Running or Quiescing or Error;
}

/// <summary>
/// Mã lý do chuyển đổi trạng thái trình duyệt và handoff đăng nhập.
/// </summary>
public static class BrowserReasonCode
{
    public const string LoginRequired = "login_required";
    public const string SessionExpired = "session_expired";
    public const string WrongAccount = "wrong_account";
    public const string AutomationBlocked = "automation_blocked";
    public const string NetworkError = "network_error";
    public const string AuthClosed = "auth_closed";
    public const string ReconcileRequired = "reconcile_required";
}

/// <summary>
/// Vai trò của 3 tab trong Shared AI profile.
/// </summary>
public static class SharedAiTabRole
{
    public const string Script = "Script";
    public const string Image = "Image";
    public const string VideoPrompt = "VideoPrompt";

    public static readonly string[] AllRoles = [Script, Image, VideoPrompt];

    public static bool IsValid(string role) =>
        role is Script or Image or VideoPrompt;
}
