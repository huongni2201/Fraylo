using System.Text;
using System.Text.RegularExpressions;
using Fraylo.Application.Prompts.Exceptions;
using Fraylo.Application.Prompts.Models;

namespace Fraylo.Application.Prompts;

/// <summary>
/// Bộ phân tích, thẩm định và render prompt template tất định (deterministic).
/// Không sử dụng eval hoặc runtime script.
/// Cú pháp: {variable_name}. Ký tự escape: {{ -> {, }} -> }.
/// </summary>
public static class PromptRenderer
{
    private static readonly Regex ValidVariableNameRegex =
        new(@"^[\p{L}\p{M}\p{N}_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Regex cho tên biến chuẩn hóa theo §2.2: Bắt đầu bằng chữ thường a-z, chỉ chứa a-z, 0-9 và _.
    /// </summary>
    public static readonly Regex StandardVariableNameRegex =
        new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Chuẩn hóa tên biến theo quy tắc §2.2: bỏ dấu tiếng Việt, viết thường, thay khoảng trắng/ký tự đặc biệt bằng _, bắt đầu bằng chữ cái.
    /// </summary>
    public static string NormalizeVariableName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "bien";
        }

        string normalized = rawName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                if (c is 'đ' or 'Đ')
                {
                    sb.Append('d');
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        string clean = sb.ToString().ToLowerInvariant();
        clean = Regex.Replace(clean, @"[^a-z0-9_]+", "_");
        clean = Regex.Replace(clean, @"_+", "_");
        clean = clean.Trim('_');

        if (clean.Length > 0 && char.IsDigit(clean[0]))
        {
            clean = "v_" + clean;
        }

        return string.IsNullOrEmpty(clean) ? "bien" : clean;
    }

    /// <summary>
    /// Trích xuất danh sách tên biến duy nhất và kiểm tra cú pháp placeholder của template.
    /// </summary>
    public static (IReadOnlyList<string> Variables, IReadOnlyList<string> MalformedPlaceholders) ParseTemplate(string? templateText)
    {
        if (string.IsNullOrEmpty(templateText))
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var detectedVariables = new HashSet<string>(StringComparer.Ordinal);
        var malformedList = new List<string>();
        int i = 0;
        int len = templateText.Length;

        while (i < len)
        {
            char c = templateText[i];

            if (c == '{')
            {
                // Kiểm tra escape {{
                if (i + 1 < len && templateText[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }

                // Bắt đầu placeholder
                int closeIndex = templateText.IndexOf('}', i + 1);

                // Kiểm tra xem có dấu { khác xuất hiện trước } hay không (lồng ngoặc bất hợp lệ)
                int nextOpenIndex = templateText.IndexOf('{', i + 1);
                if (nextOpenIndex != -1 && (closeIndex == -1 || nextOpenIndex < closeIndex))
                {
                    // Lỗi: { chưa đóng trước khi gặp { tiếp theo
                    int errorLen = Math.Min(len - i, 20);
                    malformedList.Add(templateText.Substring(i, errorLen));
                    i = nextOpenIndex;
                    continue;
                }

                if (closeIndex == -1)
                {
                    // Lỗi: { không có } đóng tới hết chuỗi
                    malformedList.Add(templateText.Substring(i));
                    break;
                }

                string varName = templateText.Substring(i + 1, closeIndex - i - 1).Trim();

                if (string.IsNullOrWhiteSpace(varName) || !ValidVariableNameRegex.IsMatch(varName))
                {
                    // Placeholder rỗng hoặc chứa ký tự không hợp lệ
                    malformedList.Add(templateText.Substring(i, closeIndex - i + 1));
                }
                else
                {
                    detectedVariables.Add(varName);
                }

                i = closeIndex + 1;
            }
            else if (c == '}')
            {
                // Kiểm tra escape }}
                if (i + 1 < len && templateText[i + 1] == '}')
                {
                    i += 2;
                    continue;
                }

                // Dấu } đơn lẻ không có { tương ứng
                malformedList.Add("}");
                i++;
            }
            else
            {
                i++;
            }
        }

        return (detectedVariables.ToList(), malformedList);
    }

    /// <summary>
    /// Thẩm định template text, khai báo biến và giá trị biến được cung cấp.
    /// </summary>
    public static PromptValidationResult Validate(
        string? templateText,
        IEnumerable<PromptVariableDefinition>? definitions = null,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        var errors = new List<string>();
        var (detectedVars, malformedPlaceholders) = ParseTemplate(templateText);

        if (string.IsNullOrWhiteSpace(templateText))
        {
            errors.Add("Nội dung template không được để trống.");
        }

        var defDict = (definitions ?? Enumerable.Empty<PromptVariableDefinition>())
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var undeclaredVars = new List<string>();
        var missingVars = new List<string>();

        // 1. Kiểm tra các biến có trong template
        foreach (var varName in detectedVars)
        {
            bool hasValue = variables != null &&
                            variables.TryGetValue(varName, out var val) &&
                            val != null;

            bool hasDef = defDict.TryGetValue(varName, out var def);

            // Nếu có định nghĩa biến nhưng biến này không được khai báo
            if (defDict.Count > 0 && !hasDef)
            {
                undeclaredVars.Add(varName);
            }

            // Kiểm tra thiếu giá trị
            if (!hasValue)
            {
                bool hasDefault = hasDef && !string.IsNullOrEmpty(def!.DefaultValue);
                if (!hasDefault)
                {
                    // Nếu không có giá trị và không có default thì bị thiếu
                    missingVars.Add(varName);
                }
            }
        }

        // 2. Kiểm tra các biến bắt buộc trong definitions có bị thiếu không
        foreach (var kvp in defDict)
        {
            var def = kvp.Value;
            if (def.IsRequired && string.IsNullOrEmpty(def.DefaultValue))
            {
                bool provided = variables != null &&
                                variables.TryGetValue(def.Name, out var val) &&
                                !string.IsNullOrEmpty(val);

                if (!provided && !detectedVars.Contains(def.Name))
                {
                    // Biến bắt buộc nhưng không xuất hiện trong template và cũng không truyền giá trị
                    // (chỉ cảnh báo hoặc đánh dấu nếu template dùng)
                }
                else if (!provided && !missingVars.Contains(def.Name))
                {
                    missingVars.Add(def.Name);
                }
            }
        }

        if (malformedPlaceholders.Count > 0)
        {
            errors.Add($"Phát hiện {malformedPlaceholders.Count} placeholder lỗi cú pháp: {string.Join(", ", malformedPlaceholders)}");
        }

        if (missingVars.Count > 0)
        {
            errors.Add($"Thiếu giá trị cho các biến bắt buộc: {string.Join(", ", missingVars)}");
        }

        if (undeclaredVars.Count > 0)
        {
            errors.Add($"Các biến xuất hiện trong template nhưng chưa được khai báo: {string.Join(", ", undeclaredVars)}");
        }

        return new PromptValidationResult
        {
            Errors = errors,
            MissingVariables = missingVars,
            UndeclaredVariables = undeclaredVars,
            MalformedPlaceholders = malformedPlaceholders,
            DetectedVariables = detectedVars
        };
    }

    /// <summary>
    /// Render nội dung prompt deterministically bằng cách thay thế các biến.
    /// Không dùng eval/script.
    /// Ném <see cref="PromptValidationException"/> nếu có placeholder lỗi hoặc thiếu biến.
    /// </summary>
    public static string Render(
        string templateText,
        IReadOnlyDictionary<string, string>? variables = null,
        IEnumerable<PromptVariableDefinition>? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(templateText);

        var validation = Validate(templateText, definitions, variables);
        if (!validation.IsValid)
        {
            string detail = string.Join("; ", validation.Errors);
            throw new PromptValidationException($"Không thể render prompt template do dữ liệu không hợp lệ: {detail}", validation.Errors);
        }

        var defDict = (definitions ?? Enumerable.Empty<PromptVariableDefinition>())
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var sb = new StringBuilder(templateText.Length * 2);
        int i = 0;
        int len = templateText.Length;

        while (i < len)
        {
            char c = templateText[i];

            if (c == '{')
            {
                // Escape {{ -> {
                if (i + 1 < len && templateText[i + 1] == '{')
                {
                    sb.Append('{');
                    i += 2;
                    continue;
                }

                int closeIndex = templateText.IndexOf('}', i + 1);
                if (closeIndex == -1)
                {
                    // Đã validate trước đó, nếu vẫn gặp thì append nguyên
                    sb.Append(c);
                    i++;
                    continue;
                }

                string varName = templateText.Substring(i + 1, closeIndex - i - 1).Trim();

                string replacement = string.Empty;
                if (variables != null && variables.TryGetValue(varName, out var val) && val != null)
                {
                    replacement = val;
                }
                else if (defDict.TryGetValue(varName, out var def) && def.DefaultValue != null)
                {
                    replacement = def.DefaultValue;
                }

                sb.Append(replacement);
                i = closeIndex + 1;
            }
            else if (c == '}')
            {
                // Escape }} -> }
                if (i + 1 < len && templateText[i + 1] == '}')
                {
                    sb.Append('}');
                    i += 2;
                    continue;
                }

                sb.Append('}');
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }
}
