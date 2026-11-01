using System.Collections.Generic;

namespace Fraylo.Contracts.Settings;

public sealed record SettingsDto(
    string DocumentsPath,
    string ImagesPath,
    string VideosPath,
    string DocumentsRootId,
    string ImagesRootId,
    string VideosRootId,
    string? ImageProvider,
    string? ScriptProvider,
    string? VideoPromptProvider,
    string ThemePreference,
    string EffectiveTheme,
    long Revision = 1,
    int MaxOpenBrowsers = 4,
    int MaxConcurrentJobs = 8
);

public sealed record SettingsSaveRequest(
    string DocumentsPath,
    string ImagesPath,
    string VideosPath,
    string? ImageProvider = null,
    string? ScriptProvider = null,
    string? VideoPromptProvider = null,
    long? ExpectedRevision = null,
    int? MaxOpenBrowsers = null,
    int? MaxConcurrentJobs = null,
    string? ClientRequestId = null
);

public sealed record BrowseFolderRequest(
    string Target, // "documents" | "images" | "videos"
    string? InitialPath = null
);

public sealed record BrowseFolderResponse(
    string? SelectedPath
);

public sealed record ValidateRootsRequest(
    string DocumentsPath,
    string ImagesPath,
    string VideosPath
);

public sealed record ValidateRootsResponse(
    bool IsValid,
    Dictionary<string, List<string>> FieldErrors
);
