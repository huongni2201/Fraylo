using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Accounts;

/// <summary>
/// DTO đại diện cho tài khoản Provider AI toàn hệ thống (ChatGPT, Gemini, MuseAI)
/// theo execution-contract-v1.1 và plan 1.2 §3.2.
/// </summary>
public sealed record ProviderAccountDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("providerKey")] string ProviderKey,
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("verifiedIdentity")] string? VerifiedIdentity,
    [property: JsonPropertyName("identityVerifiedAtUtc")] string? IdentityVerifiedAtUtc,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("lifecycle")] string Lifecycle,
    [property: JsonPropertyName("maxConcurrentJobs")] int MaxConcurrentJobs,
    [property: JsonPropertyName("maxTabs")] int MaxTabs,
    [property: JsonPropertyName("perTaskLimitsJson")] string? PerTaskLimitsJson,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc,
    [property: JsonPropertyName("activeJobCount")] int ActiveJobCount = 0,
    [property: JsonPropertyName("sessionStatus")] string? SessionStatus = null
);

public sealed record ProviderAccountListRequest(
    [property: JsonPropertyName("providerKey")] string? ProviderKey = null,
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("lifecycle")] string? Lifecycle = null
);

public sealed record ProviderAccountListResponseData(
    [property: JsonPropertyName("accounts")] IReadOnlyList<ProviderAccountDto> Accounts
);

public sealed record CreateProviderAccountRequest(
    [property: JsonPropertyName("providerKey")] string ProviderKey,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("profileId")] string? ProfileId = null,
    [property: JsonPropertyName("maxConcurrentJobs")] int? MaxConcurrentJobs = null,
    [property: JsonPropertyName("maxTabs")] int? MaxTabs = null,
    [property: JsonPropertyName("perTaskLimitsJson")] string? PerTaskLimitsJson = null
);

public sealed record CreateProviderAccountResponseData(
    [property: JsonPropertyName("account")] ProviderAccountDto Account
);

public sealed record UpdateProviderAccountRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("maxConcurrentJobs")] int MaxConcurrentJobs,
    [property: JsonPropertyName("maxTabs")] int MaxTabs,
    [property: JsonPropertyName("perTaskLimitsJson")] string? PerTaskLimitsJson,
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision
);

public sealed record UpdateProviderAccountResponseData(
    [property: JsonPropertyName("account")] ProviderAccountDto Account
);

public sealed record ProbeProviderAccountSessionRequest(
    [property: JsonPropertyName("id")] string Id
);

public sealed record ProbeProviderAccountSessionResponseData(
    [property: JsonPropertyName("account")] ProviderAccountDto Account,
    [property: JsonPropertyName("isVerified")] bool IsVerified,
    [property: JsonPropertyName("sessionStatus")] string SessionStatus,
    [property: JsonPropertyName("accountIdentity")] string? AccountIdentity,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage = null
);

public sealed record EnableProviderAccountRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("expectedRevision")] long? ExpectedRevision = null
);

public sealed record EnableProviderAccountResponseData(
    [property: JsonPropertyName("account")] ProviderAccountDto Account
);

public sealed record DeleteProviderAccountRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("expectedRevision")] long? ExpectedRevision = null
);

public sealed record DeleteProviderAccountResponseData(
    [property: JsonPropertyName("account")] ProviderAccountDto Account
);
