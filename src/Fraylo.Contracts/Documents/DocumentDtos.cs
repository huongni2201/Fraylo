namespace Fraylo.Contracts.Documents;

public sealed record DocumentExportRequest(
    string SceneId,
    string Format = "txt",
    string? CustomFileName = null
);

public sealed record DocumentExportResponse(
    string AssetId,
    string DisplayName,
    string StorageRootId,
    string RelativePath,
    long ByteSize
);
