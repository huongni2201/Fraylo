using System.Text.Json;
using System.Text.RegularExpressions;
using Fraylo.Contracts.Content;

namespace Fraylo.Application.Content;

/// <summary>
/// Trình phân tích cú pháp Candidate và kiểm tra tính tuân thủ hợp đồng đầu ra
/// từ phản hồi của ChatGPT/Gemini Web theo C11/C12/contracts.md.
/// </summary>
public static class ContentCandidateParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex JsonCodeBlockRegex = new(
        @"```(?:json)?\s*([\s\S]*?)\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Trích xuất chuỗi JSON sạch từ phản hồi AI (hỗ trợ cả văn bản thường và bọc trong markdown codeblock).
    /// </summary>
    public static string ExtractJsonPayload(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var match = JsonCodeBlockRegex.Match(rawText);
        if (match.Success && match.Groups.Count > 1)
        {
            var extracted = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(extracted))
            {
                return extracted;
            }
        }

        // innerText của khối code trên web AI không có dấu ```, thường kèm nhãn "JSON"/"Copy" trước và sau JSON.
        var trimmed = rawText.Trim();
        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        return first >= 0 && last > first ? trimmed[first..(last + 1)] : trimmed;
    }

    /// <summary>
    /// Parse và thẩm định kết quả cho operation SuggestIdeas.
    /// </summary>
    public static ContentCandidateResult<SuggestIdeasOutputDto> ParseSuggestIdeas(string rawText)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            errors.Add("Phản hồi từ AI web trống rỗng.");
            return new ContentCandidateResult<SuggestIdeasOutputDto>(rawText ?? string.Empty, null, errors, false);
        }

        string jsonPayload = ExtractJsonPayload(rawText);
        try
        {
            var candidate = JsonSerializer.Deserialize<SuggestIdeasOutputDto>(jsonPayload, JsonOptions);
            if (candidate == null)
            {
                errors.Add("Không thể giải mã dữ liệu JSON của ý tưởng đề xuất.");
                return new ContentCandidateResult<SuggestIdeasOutputDto>(rawText, null, errors, false);
            }

            if (candidate.Ideas == null || candidate.Ideas.Count == 0)
            {
                errors.Add("Danh sách ý tưởng đề xuất rỗng.");
            }
            else
            {
                for (int i = 0; i < candidate.Ideas.Count; i++)
                {
                    var idea = candidate.Ideas[i];
                    if (string.IsNullOrWhiteSpace(idea.Title))
                    {
                        errors.Add($"Ý tưởng thứ {i + 1} thiếu tiêu đề (title).");
                    }
                    if (string.IsNullOrWhiteSpace(idea.Hook))
                    {
                        errors.Add($"Ý tưởng thứ {i + 1} thiếu hook.");
                    }
                    if (idea.TikTokPost == null || string.IsNullOrWhiteSpace(idea.TikTokPost.Caption))
                    {
                        errors.Add($"Ý tưởng thứ {i + 1} thiếu bài đăng TikTok (caption).");
                    }
                }
            }

            bool isCompliant = errors.Count == 0;
            return new ContentCandidateResult<SuggestIdeasOutputDto>(rawText, candidate, errors, isCompliant);
        }
        catch (JsonException ex)
        {
            errors.Add($"Lỗi định dạng JSON trong phản hồi AI: {ex.Message}");
            return new ContentCandidateResult<SuggestIdeasOutputDto>(rawText, null, errors, false);
        }
    }

    /// <summary>
    /// Parse và thẩm định kết quả cho operation GenerateScript.
    /// </summary>
    public static ContentCandidateResult<GenerateScriptOutputDto> ParseGenerateScript(string rawText)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            errors.Add("Phản hồi từ AI web trống rỗng.");
            return new ContentCandidateResult<GenerateScriptOutputDto>(rawText ?? string.Empty, null, errors, false);
        }

        string jsonPayload = ExtractJsonPayload(rawText);
        try
        {
            var candidate = JsonSerializer.Deserialize<GenerateScriptOutputDto>(jsonPayload, JsonOptions);
            if (candidate == null)
            {
                errors.Add("Không thể giải mã dữ liệu JSON của kịch bản.");
                return new ContentCandidateResult<GenerateScriptOutputDto>(rawText, null, errors, false);
            }

            if (string.IsNullOrWhiteSpace(candidate.Title))
            {
                errors.Add("Kịch bản thiếu tiêu đề (title).");
            }

            if (candidate.Scenes == null || candidate.Scenes.Count == 0)
            {
                errors.Add("Kịch bản không chứa bất kỳ cảnh quay nào (scenes).");
            }
            else
            {
                for (int i = 0; i < candidate.Scenes.Count; i++)
                {
                    var scene = candidate.Scenes[i];
                    if (string.IsNullOrWhiteSpace(scene.Name))
                    {
                        errors.Add($"Cảnh {i + 1} thiếu tên cảnh.");
                    }
                    if (string.IsNullOrWhiteSpace(scene.Description))
                    {
                        errors.Add($"Cảnh {i + 1} thiếu mô tả hình ảnh/hành động.");
                    }
                }
            }

            if (candidate.TikTokPost == null || string.IsNullOrWhiteSpace(candidate.TikTokPost.Caption))
            {
                errors.Add("Kịch bản thiếu bài đăng TikTok cấp project (caption).");
            }

            bool isCompliant = errors.Count == 0;
            return new ContentCandidateResult<GenerateScriptOutputDto>(rawText, candidate, errors, isCompliant);
        }
        catch (JsonException ex)
        {
            errors.Add($"Lỗi định dạng JSON trong phản hồi kịch bản: {ex.Message}");
            return new ContentCandidateResult<GenerateScriptOutputDto>(rawText, null, errors, false);
        }
    }

    /// <summary>
    /// Parse và thẩm định kết quả cho operation GenerateVideoPrompts.
    /// </summary>
    public static ContentCandidateResult<GenerateVideoPromptsOutputDto> ParseGenerateVideoPrompts(string rawText)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            errors.Add("Phản hồi từ AI web trống rỗng.");
            return new ContentCandidateResult<GenerateVideoPromptsOutputDto>(rawText ?? string.Empty, null, errors, false);
        }

        string jsonPayload = ExtractJsonPayload(rawText);
        try
        {
            var candidate = JsonSerializer.Deserialize<GenerateVideoPromptsOutputDto>(jsonPayload, JsonOptions);
            if (candidate == null)
            {
                errors.Add("Không thể giải mã dữ liệu JSON của video prompt.");
                return new ContentCandidateResult<GenerateVideoPromptsOutputDto>(rawText, null, errors, false);
            }

            if (candidate.Scenes == null || candidate.Scenes.Count == 0)
            {
                errors.Add("Không có prompt video cho bất kỳ cảnh nào.");
            }
            else
            {
                for (int i = 0; i < candidate.Scenes.Count; i++)
                {
                    var scene = candidate.Scenes[i];
                    if (string.IsNullOrWhiteSpace(scene.PromptEn))
                    {
                        errors.Add($"Cảnh thứ {i + 1} thiếu prompt tiếng Anh (promptEn).");
                    }
                }
            }

            if (candidate.TikTokPost == null || string.IsNullOrWhiteSpace(candidate.TikTokPost.Caption))
            {
                errors.Add("Thiếu bài đăng TikTok phương án cấp project.");
            }

            bool isCompliant = errors.Count == 0;
            return new ContentCandidateResult<GenerateVideoPromptsOutputDto>(rawText, candidate, errors, isCompliant);
        }
        catch (JsonException ex)
        {
            errors.Add($"Lỗi định dạng JSON trong phản hồi prompt video: {ex.Message}");
            return new ContentCandidateResult<GenerateVideoPromptsOutputDto>(rawText, null, errors, false);
        }
    }

    /// <summary>
    /// Parse và thẩm định nội dung kịch bản theo schema { idea, hook, video_script, video_prompt, tiktok_content }.
    /// </summary>
    public static ContentCandidateResult<VideoContentItemDto> ParseVideoContent(
        string rawText,
        string projectId = "",
        string? sceneId = null,
        int sequenceOrder = 1,
        string? imageAssetId = null)
    {
        var errors = new List<string>();
        string nowIso = DateTimeOffset.UtcNow.ToString("o");

        if (string.IsNullOrWhiteSpace(rawText))
        {
            errors.Add("Phản hồi từ AI trống rỗng.");
            return new ContentCandidateResult<VideoContentItemDto>(
                rawText ?? string.Empty,
                null,
                errors,
                false);
        }

        string jsonPayload = ExtractJsonPayload(rawText);
        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Phản hồi không phải đối tượng JSON.");
            }

            string GetString(params string[] propertyNames)
            {
                foreach (var name in propertyNames)
                {
                    if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                    }
                }
                return string.Empty;
            }

            string idea = GetString("idea", "title", "concept", "y_tuong");
            string hook = GetString("hook", "cau_mo_dau", "intro");
            string videoScript = GetString("video_script", "script", "kich_ban", "videoScript");
            string videoPrompt = GetString("video_prompt", "prompt", "promptEn", "videoPrompt");
            string tiktokContent = GetString("tiktok_content", "caption", "tiktok_post", "tiktokPost", "tiktokContent");

            // Thiếu trường nào thì để trống, không bịa nội dung: prompt trống sẽ bị chặn khi tạo video.
            if (string.IsNullOrWhiteSpace(idea))
            {
                idea = hook;
            }
            if (string.IsNullOrWhiteSpace(videoScript))
            {
                videoScript = rawText.Length > 200 ? rawText[..200] : rawText;
            }
            if (string.IsNullOrWhiteSpace(videoPrompt))
            {
                errors.Add("Phản hồi thiếu video_prompt: không có prompt để render.");
            }

            var item = new VideoContentItemDto(
                Id: Guid.NewGuid().ToString("D").ToLowerInvariant(),
                ProjectId: projectId,
                SceneId: sceneId,
                SequenceOrder: sequenceOrder,
                Idea: idea,
                Hook: hook,
                VideoScript: videoScript,
                VideoPrompt: videoPrompt,
                TikTokContent: tiktokContent,
                ImageAssetId: imageAssetId,
                VideoAssetId: null,
                AudioAssetId: null,
                Status: "Draft",
                CreatedAtUtc: nowIso,
                UpdatedAtUtc: nowIso
            );

            return new ContentCandidateResult<VideoContentItemDto>(rawText, item, errors, errors.Count == 0);
        }
        catch (JsonException ex)
        {
            // Văn bản thường không phải prompt: giữ nguyên để người dùng xem/sửa, không dùng làm prompt render.
            var item = new VideoContentItemDto(
                Id: Guid.NewGuid().ToString("D").ToLowerInvariant(),
                ProjectId: projectId,
                SceneId: sceneId,
                SequenceOrder: sequenceOrder,
                Idea: string.Empty,
                Hook: string.Empty,
                VideoScript: rawText.Trim(),
                VideoPrompt: string.Empty,
                TikTokContent: string.Empty,
                ImageAssetId: imageAssetId,
                VideoAssetId: null,
                AudioAssetId: null,
                Status: "Draft",
                CreatedAtUtc: nowIso,
                UpdatedAtUtc: nowIso
            );
            errors.Add($"Phản hồi không phải JSON hợp lệ, cần người dùng kiểm tra: {ex.Message}");
            return new ContentCandidateResult<VideoContentItemDto>(rawText, item, errors, false);
        }
    }
}
