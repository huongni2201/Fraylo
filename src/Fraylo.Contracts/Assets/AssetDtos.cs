namespace Fraylo.Contracts.Assets;

public sealed record AssetDto(
    string AssetId,
    string? ProjectId,
    string AssetType,
    string StorageRootId,
    string RelativePath,
    long ByteSize,
    string Sha256Hash,
    string Status,
    string CreatedAtUtc,
    string? MimeType = null
);

public sealed record ImportAudioRequest(
    string ProjectId,
    string SceneId,
    string? SourceFilePath = null // Dùng khi programmatic import/smoke test hoặc truyền trực tiếp từ dialog
);

public sealed record ImportAudioResponse(
    AssetDto? Asset
);

public sealed record SelectReferenceImageRequest(
    string ProjectId,
    string SceneId,
    string? SourceFilePath = null
);

public sealed record SelectReferenceImageResponse(
    AssetDto? Asset
);

public sealed record UploadAssetRequest(
    string? ProjectId = null,
    string AssetType = "Image", // Image | Audio | Video (Video chỉ chọn qua picker host)
    string? FileName = null,
    string? Base64Data = null,
    string? SourceFilePath = null,
    string? StorageRootId = null, // Root đã chụp trong job; null thì dùng Settings hiện hành
    bool Multiple = false // Chỉ áp dụng cho Image khi host mở hộp thoại: cho chọn nhiều tệp một lần
);

public sealed record UploadAssetResponse(
    AssetDto? Asset,
    bool Canceled = false,
    IReadOnlyList<AssetDto>? Assets = null, // Có khi Multiple: mọi tệp tải thành công
    IReadOnlyList<string>? Errors = null // Có khi Multiple: lỗi từng tệp không tải được
);

public sealed record GetAssetMetadataRequest(
    string AssetId
);

public sealed record OpenFolderRequest(
    string AssetId
);

public sealed record OpenFolderResponse(
    bool Success,
    string? FolderPath = null
);

public sealed record ListAssetsRequest(
    string? ProjectId = null,
    string? AssetType = null,
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    bool IncludeShared = false
);

public sealed record ListAssetsResponse(
    IReadOnlyList<AssetDto> Assets,
    int TotalCount,
    int Page,
    int PageSize
);

public sealed record GetAssetPreviewRequest(
    string AssetId
);

public sealed record GetAssetPreviewResponse(
    string AssetId,
    string AssetType,
    string RelativePath,
    string? MimeType,
    long ByteSize,
    string Sha256Hash,
    bool FileExists,
    bool IsRootOffline,
    string Status
);
