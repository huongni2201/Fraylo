using System;
using System.Collections.Generic;

namespace Fraylo.Contracts.Providers;

public static class ProviderKeys
{
    public const string ChatGPTWeb = "chatgpt-web";
    public const string GeminiWeb = "gemini-web";
    public const string MuseAI = "museai";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ChatGPTWeb,
        GeminiWeb,
        MuseAI
    };

    public static readonly IReadOnlyList<string> ContentProviders = new[]
    {
        ChatGPTWeb,
        GeminiWeb
    };

    public static bool IsValid(string? providerKey) =>
        !string.IsNullOrWhiteSpace(providerKey) &&
        (string.Equals(providerKey, ChatGPTWeb, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(providerKey, GeminiWeb, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(providerKey, MuseAI, StringComparison.OrdinalIgnoreCase));

    public static bool IsValidContentProvider(string? providerKey) =>
        !string.IsNullOrWhiteSpace(providerKey) &&
        (string.Equals(providerKey, ChatGPTWeb, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(providerKey, GeminiWeb, StringComparison.OrdinalIgnoreCase));
}

public static class ProviderVerificationStatus
{
    public const string Verified = "VERIFIED";
    public const string Trial = "TRIAL";
    public const string NotVerified = "NOT_VERIFIED";
    public const string Unsupported = "UNSUPPORTED";
}

public static class ProviderExecutionMode
{
    public const string Automation = "automation";
    public const string Unavailable = "unavailable";
}

public sealed record ProviderTaskCapabilityDto(
    string ProviderKey,
    string TaskType,
    string VerificationStatus,
    bool AutomationSupported,
    bool ManualSupported,
    bool RequiresReferenceImage,
    int MaxInputLength,
    string? EvidenceId,
    string? VerifiedAtUtc,
    int ConfigVersion,
    bool SupportsDualReferenceImages = false,
    bool SupportsImageInputForPrompt = false,
    bool SupportsNoAudioVideo = false,
    string? AudioBehaviorDescription = null,
    IReadOnlyList<int>? SupportedDurations = null,
    bool SupportsMultiShot = false
);

public sealed record ProviderAccountSessionSummaryDto(
    string ProviderAccountId,
    string ProviderKey,
    string ProfileId,
    string DisplayName,
    string? VerifiedIdentity,
    string SessionStatus,
    bool Enabled,
    int MaxConcurrentJobs,
    int ActiveJobs
);

public sealed record ProviderListResponseData(
    IReadOnlyList<ProviderTaskCapabilityDto> Tasks,
    IReadOnlyList<ProviderAccountSessionSummaryDto> Accounts
);

public sealed record ProviderGetCapabilitiesRequest(
    string ProviderKey,
    string? TaskType = null
);

public sealed record ProviderGetCapabilitiesResponseData(
    string ProviderKey,
    IReadOnlyList<ProviderTaskCapabilityDto> Capabilities
);
