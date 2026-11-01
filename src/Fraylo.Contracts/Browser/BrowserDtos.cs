using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Browser;

public sealed record BrowserProfileDto(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("accountIdentity")] string? AccountIdentity,
    [property: JsonPropertyName("visibilityState")] string VisibilityState,
    [property: JsonPropertyName("sessionStatus")] string SessionStatus,
    [property: JsonPropertyName("operationalState")] string OperationalState,
    [property: JsonPropertyName("activeJobCount")] int ActiveJobCount,
    [property: JsonPropertyName("activeTabs")] IReadOnlyList<string> ActiveTabs,
    [property: JsonPropertyName("providerKey")] string? ProviderKey = null,
    [property: JsonPropertyName("displayName")] string? DisplayName = null,
    [property: JsonPropertyName("lifecycle")] string Lifecycle = "Active");

public sealed record BrowserListProfilesRequest();

public sealed record BrowserListProfilesResponseData(
    [property: JsonPropertyName("profiles")] IReadOnlyList<BrowserProfileDto> Profiles);

public sealed record BrowserRequestLoginRequest(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("targetUrl")] string? TargetUrl = null,
    [property: JsonPropertyName("providerKey")] string? ProviderKey = null);

public sealed record BrowserRequestLoginResponseData(
    [property: JsonPropertyName("queued")] bool Queued,
    [property: JsonPropertyName("visibilityState")] string VisibilityState,
    [property: JsonPropertyName("currentHolder")] string? CurrentHolder);

public sealed record BrowserCancelLoginRequest(
    [property: JsonPropertyName("profileId")] string ProfileId);

public sealed record BrowserCancelLoginResponseData(
    [property: JsonPropertyName("canceled")] bool Canceled);

public sealed record BrowserCheckSessionRequest(
    [property: JsonPropertyName("profileId")] string ProfileId);

public sealed record BrowserCheckSessionResponseData(
    [property: JsonPropertyName("sessionStatus")] string SessionStatus,
    [property: JsonPropertyName("identityMatches")] bool? IdentityMatches);

public sealed record BrowserProfileStateChangedPayload(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("visibilityState")] string VisibilityState,
    [property: JsonPropertyName("sessionStatus")] string SessionStatus,
    [property: JsonPropertyName("operationalState")] string OperationalState,
    [property: JsonPropertyName("reasonCode")] string? ReasonCode);
