namespace TiToAI.Infrastructure.Persistence.Entities;

/// <summary>
/// Nhóm dự án: gom các Project cùng ngành hàng và giữ hướng dẫn prompt/biến dùng chung.
/// </summary>
public class ProjectGroup
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ScriptGuidance { get; set; } = string.Empty;
    public string VideoGuidance { get; set; } = string.Empty;
    public string PromptVariablesJson { get; set; } = "{}";
    public string? ModelAssetId { get; set; }
    public string? ProductAssetId { get; set; }
    public string? ReadyImageAssetId { get; set; }
    public string? AudioAssetId { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;
}
