namespace Fraylo.Infrastructure.Persistence.Entities;

/// <summary>Nháp lô đăng nhóm. Nhóm/ảnh lưu dạng JSON mảng id và được kiểm lại khi tạo lô, nên xóa ảnh/nhóm không bị nháp chặn.</summary>
public sealed class FacebookDraft
{
    public string? DueAtUtc { get; set; } = null;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool RewriteContent { get; set; } = true;
    public string TargetIdsJson { get; set; } = "[]";
    public string ImageAssetIdsJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;
}
