using System.Text.Json;

namespace TiToAI.Application.Prompts;

/// <summary>
/// Hướng dẫn bổ sung của Nhóm và Dự án: văn bản thuần nối sau mẫu chung, không chứa biến.
/// Mẫu chung là nơi duy nhất điền biến, nên khuôn đầu ra và biến không bị ghi đè theo từng nhóm.
/// </summary>
public static class PromptGuidance
{
    public const int MaxLength = 4000;

    /// <summary>
    /// Trả về thông báo lỗi, hoặc null khi hợp lệ.
    /// </summary>
    public static string? Validate(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Trim().Length > MaxLength) return $"{label} không được vượt quá {MaxLength} ký tự.";

        var (variables, malformed) = PromptRenderer.ParseTemplate(text);
        if (variables.Count > 0 || malformed.Count > 0)
        {
            return $"{label} không được chứa biến hoặc dấu ngoặc nhọn; biến chỉ điền trong mẫu chung.";
        }

        return null;
    }

    /// <summary>
    /// Trả về thông báo lỗi khi có tên biến không hợp lệ, hoặc null khi hợp lệ.
    /// </summary>
    public static string? ValidateVariables(IReadOnlyDictionary<string, string>? variables)
    {
        var invalid = variables?.Keys.FirstOrDefault(name => name.Trim().Any(ch => char.IsWhiteSpace(ch) || ch is '{' or '}'));
        return invalid is null ? null : $"Tên biến '{invalid}' không được chứa khoảng trắng hoặc dấu ngoặc nhọn.";
    }

    /// <summary>
    /// Cắt khoảng trắng; chuỗi rỗng thành null.
    /// </summary>
    public static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Bỏ biến có tên hoặc giá trị rỗng, cắt khoảng trắng; không có biến nào thì trả về null.
    /// </summary>
    public static Dictionary<string, string>? NormalizeVariables(IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null) return null;

        var result = variables
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Key.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.Ordinal);
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Đọc JSON biến đã lưu; JSON rỗng hoặc hỏng coi như không có biến.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseVariablesJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
