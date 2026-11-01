namespace Fraylo.Contracts.Accounts;

public sealed record ProfileIdentityResolutionResult(
    bool IsSuccess,
    string? ProfileId,
    string? Provider,
    string? AccountIdentity,
    string? SessionStatus,
    string? ErrorMessage)
{
    public static ProfileIdentityResolutionResult Success(
        string profileId,
        string provider,
        string accountIdentity,
        string sessionStatus) =>
        new(true, profileId, provider, accountIdentity, sessionStatus, null);

    public static ProfileIdentityResolutionResult Failed(string errorMessage) =>
        new(false, null, null, null, null, errorMessage);
}

/// <summary>
/// Interface phân giải và thẩm định danh tính hồ sơ trình duyệt dùng chung tại Contracts/Accounts.
/// T07 sử dụng để xác thực tài khoản trước khi link; T13 hiện thực thông qua ProfileRegistry / SessionProbe.
/// </summary>
public interface IProfileIdentityResolver
{
    Task<ProfileIdentityResolutionResult> ResolveProfileIdentityAsync(
        string profileId,
        string provider,
        CancellationToken cancellationToken = default);
}
