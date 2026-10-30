namespace TiToAI.Infrastructure.Persistence.Entities;

public class Asset
{
    public string Id { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string? GroupId { get; set; } // Thuộc Nhóm dùng chung; không đồng thời có ProjectId
    public string StorageRootId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty; // Document | Image | Video | Audio
    public string? MimeType { get; set; }
    public long ByteSize { get; set; }
    public string Sha256Hash { get; set; } = string.Empty;
    public string Status { get; set; } = "Linked"; // Linked | Unlinked
    public string CreatedAtUtc { get; set; } = string.Empty;

    public Project? Project { get; set; }
}
