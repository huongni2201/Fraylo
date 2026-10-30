namespace TiToAI.Application.Prompts;

/// <summary>
/// Prompt riêng của Nhóm và Dự án (lưu ở trường *Guidance). Khi có, prompt này thay hẳn mẫu ở mức thấp hơn
/// (Dự án &gt; Nhóm &gt; mẫu toàn cục), không ghép và không điền biến.
/// </summary>
public static class PromptGuidance
{
    public const int MaxLength = 20000;

    /// <summary>
    /// Trả về thông báo lỗi, hoặc null khi hợp lệ.
    /// </summary>
    public static string? Validate(string? text, string label) =>
        text?.Trim().Length > MaxLength ? $"{label} không được vượt quá {MaxLength} ký tự." : null;

    /// <summary>
    /// Cắt khoảng trắng; chuỗi rỗng thành null.
    /// </summary>
    public static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
