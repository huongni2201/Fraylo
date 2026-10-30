using System.Text.Json;
using TiToAI.Application.Content;
using TiToAI.Application.Prompts;
using TiToAI.Contracts.Content;

namespace TiToAI.Desktop.SmokeChecks;

/// <summary>
/// Kiểm thử khói cho tính năng Tạo nội dung qua AI Web (ChatGPT/Gemini Web) theo AC0/AC1/C01-C14.
/// </summary>
public static class AiWebContentCreationSmokeTests
{
    public static void RunTests()
    {
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine("   TiToAI — AI Web Content Creation Smoke Checks (AC0 & AC1)     ");
        Console.WriteLine("=================================================================");

        TestContentCandidateParser_GenerateScript_C01_C02();
        TestContentCandidateParser_GenerateVideoPrompts_C01();
        TestContentCandidateParser_SuggestIdeas_C04();
        TestContentCandidateParser_ResilienceAndErrors_C11_C12();
        TestAnalyticsContextMetricNullDistinctFromZero_C05();
        TestTikTokPostProjectScope_C13();

        Console.WriteLine("=================================================================");
        Console.WriteLine("   TẤT CẢ KIỂM THỬ KHÓI AI WEB CONTENT CREATION ĐỀU PASS 100%!  ");
        Console.WriteLine("=================================================================");
    }

    /// <summary>
    /// C01, C02: Parser kịch bản (GenerateScript) từ phản hồi bọc trong markdown codeblock.
    /// </summary>
    private static void TestContentCandidateParser_GenerateScript_C01_C02()
    {
        Console.Write("[RUN] Test C01/C02: Parser GenerateScript bọc markdown codeblock ... ");

        string aiResponse = """
            Dưới đây là kịch bản video ngắn hoàn chỉnh theo yêu cầu:
            ```json
            {
              "title": "5 mẹo tiết kiệm thời gian buổi sáng",
              "concept": "Hướng dẫn thực tế giúp người bận rộn bắt đầu ngày mới hiệu quả",
              "assumptions": ["Người xem là dân văn phòng", "Thời lượng khoảng 45 giây"],
              "continuityNotes": "Nhân vật nữ chính mặc áo sơ mi xanh throughout",
              "scenes": [
                {
                  "sceneKey": "scene_1",
                  "order": 1,
                  "name": "Báo thức và sự chần chừ",
                  "description": "Cận cảnh bàn tay tắt chuông báo thức điện thoại",
                  "targetDurationSeconds": 5,
                  "imagePrompt": "Close-up of a hand tapping alarm off on smartphone screen, morning sunlight"
                },
                {
                  "sceneKey": "scene_2",
                  "order": 2,
                  "name": "Chuẩn bị trang phục tối ưu",
                  "description": "Tủ quần áo đã được treo sẵn bộ đồ cần mặc",
                  "targetDurationSeconds": 8,
                  "imagePrompt": "Organized wardrobe with pre-selected work outfit hanging cleanly, modern minimalist bedroom"
                }
              ],
              "tiktokPost": {
                "caption": "Buổi sáng của bạn có thường xuyên vội vã? Thử ngay 5 mẹo này nhé! #productivity #morningroutine #lifehacks",
                "hashtags": ["#productivity", "#morningroutine", "#lifehacks"]
              }
            }
            ```
            Hy vọng bạn hài lòng với kịch bản này!
            """;

        var result = ContentCandidateParser.ParseGenerateScript(aiResponse);

        if (!result.IsCompliant || result.Candidate == null)
        {
            throw new InvalidOperationException($"Kỳ vọng parser tuân thủ hợp đồng, thực tế lỗi: {string.Join(", ", result.Errors)}");
        }

        if (result.Candidate.Scenes.Count != 2)
        {
            throw new InvalidOperationException($"Kỳ vọng 2 cảnh quay, thực tế: {result.Candidate.Scenes.Count}");
        }

        if (string.IsNullOrWhiteSpace(result.Candidate.TikTokPost.Caption))
        {
            throw new InvalidOperationException("Kỳ vọng có TikTokPost caption cho kịch bản.");
        }

        Console.WriteLine("PASS");
    }

    /// <summary>
    /// C01: Parser prompt video tiếng Anh (GenerateVideoPrompts).
    /// </summary>
    private static void TestContentCandidateParser_GenerateVideoPrompts_C01()
    {
        Console.Write("[RUN] Test C01: Parser GenerateVideoPrompts per-scene ... ");

        string aiResponse = """
            ```json
            {
              "scenes": [
                {
                  "sceneKey": "scene_1",
                  "promptEn": "Cinematic slow motion push in on a sleek smartphone screen displaying an alarm at 6:00 AM, soft golden hour morning light streaming through sheer curtains, photorealistic details.",
                  "continuityNotes": "Soft ambient morning atmosphere"
                },
                {
                  "sceneKey": "scene_2",
                  "promptEn": "Smooth tracking shot moving across a minimalist open wardrobe showcasing a neatly ironed navy business suit, clean modern aesthetic.",
                  "continuityNotes": "Maintain natural warm color grading"
                }
              ],
              "tiktokPost": {
                "caption": "Bắt đầu ngày mới đầy năng lượng cùng routine tối ưu! #morningmotivation #shorts",
                "hashtags": ["#morningmotivation", "#shorts"]
              }
            }
            ```
            """;

        var result = ContentCandidateParser.ParseGenerateVideoPrompts(aiResponse);

        if (!result.IsCompliant || result.Candidate == null)
        {
            throw new InvalidOperationException($"Kỳ vọng parser tuân thủ hợp đồng, thực tế lỗi: {string.Join(", ", result.Errors)}");
        }

        if (result.Candidate.Scenes.Count != 2)
        {
            throw new InvalidOperationException($"Kỳ vọng 2 scenes prompt, thực tế: {result.Candidate.Scenes.Count}");
        }

        if (!result.Candidate.Scenes[0].PromptEn.Contains("Cinematic slow motion"))
        {
            throw new InvalidOperationException("Nội dung promptEn scene 1 không chính xác.");
        }

        Console.WriteLine("PASS");
    }

    /// <summary>
    /// C04: Parser SuggestIdeas kèm evidenceRefs và tiktokPost.
    /// </summary>
    private static void TestContentCandidateParser_SuggestIdeas_C04()
    {
        Console.Write("[RUN] Test C04: Parser SuggestIdeas kèm phân tích và evidence ... ");

        string aiResponse = """
            {
              "analysis": {
                "observations": ["Video thời lượng 30-45s có lượt giữ chân cao hơn 25%", "Chủ đề mẹo vặt buổi sáng thu hút nhiều bình luận nhất"],
                "limitations": ["Dữ liệu 14 ngày gần nhất chưa có chỉ số retention sau giây 30"],
                "analyzedVideoCount": 10,
                "periodDescription": "14 ngày qua (12/10 - 26/10)"
              },
              "ideas": [
                {
                  "ideaId": "idea_001",
                  "title": "3 thói quen buổi sáng hủy hoại sự tập trung",
                  "hook": "Dừng ngay việc làm này trong 10 phút đầu tiên sau khi thức dậy!",
                  "summary": "Phân tích tác hại của việc kiểm tra mạng xã hội ngay khi mở mắt và giải pháp thay thế.",
                  "suggestedDurationSeconds": 40,
                  "rationale": "Dựa trên video VID-102 có lượng chia sẻ cao đột biến khi nói về thói quen xấu.",
                  "evidenceRefs": ["VID-102", "VID-105"],
                  "hypothesis": "Video cảnh báo thói quen sẽ tăng tỷ lệ xem lại ít nhất 15%",
                  "tiktokPost": {
                    "caption": "Bạn có mắc phải thói quen số 2 không? Xem ngay nhé! #morninghabits #productivity #focus",
                    "hashtags": ["#morninghabits", "#productivity", "#focus"]
                  }
                }
              ]
            }
            """;

        var result = ContentCandidateParser.ParseSuggestIdeas(aiResponse);

        if (!result.IsCompliant || result.Candidate == null)
        {
            throw new InvalidOperationException($"Kỳ vọng parser tuân thủ hợp đồng, thực tế lỗi: {string.Join(", ", result.Errors)}");
        }

        if (result.Candidate.Ideas.Count != 1)
        {
            throw new InvalidOperationException($"Kỳ vọng 1 ý tưởng, thực tế: {result.Candidate.Ideas.Count}");
        }

        var idea = result.Candidate.Ideas[0];
        if (idea.EvidenceRefs.Count != 2 || idea.EvidenceRefs[0] != "VID-102")
        {
            throw new InvalidOperationException("Kỳ vọng evidenceRefs chứa đúng 2 mã tham chiếu.");
        }

        Console.WriteLine("PASS");
    }

    /// <summary>
    /// C11, C12: Khả năng chống chịu khi JSON lỗi, thiếu trường hoặc rỗng (bảo toàn rawText, không crash).
    /// </summary>
    private static void TestContentCandidateParser_ResilienceAndErrors_C11_C12()
    {
        Console.Write("[RUN] Test C11/C12: Chống chịu JSON lỗi cú pháp, rỗng hoặc thiếu trường ... ");

        // 1. Phản hồi rỗng
        var emptyRes = ContentCandidateParser.ParseGenerateScript("");
        if (emptyRes.IsCompliant || emptyRes.Errors.Count == 0)
        {
            throw new InvalidOperationException("Kỳ vọng phản hồi rỗng bị đánh dấu không tuân thủ.");
        }

        // 2. Phản hồi JSON hỏng cú pháp
        string malformedJson = "{ title: 'kịch bản không hợp lệ', scenes: [";
        var malformedRes = ContentCandidateParser.ParseGenerateScript(malformedJson);
        if (malformedRes.IsCompliant || malformedRes.RawText != malformedJson || malformedRes.Errors.Count == 0)
        {
            throw new InvalidOperationException("Kỳ vọng JSON hỏng cú pháp giữ rawText và ghi nhận lỗi.");
        }

        // 3. Thiếu trường bắt buộc (thiếu scenes và caption)
        string missingFieldsJson = "{\"title\": \"Kịch bản thiếu trường\"}";
        var missingRes = ContentCandidateParser.ParseGenerateScript(missingFieldsJson);
        if (missingRes.IsCompliant || missingRes.Errors.Count < 2)
        {
            throw new InvalidOperationException("Kỳ vọng phát hiện thiếu scenes và thiếu tiktokPost caption.");
        }

        // 4. Văn bản thường (ví dụ lời từ chối) không được thành prompt render
        const string refusal = "Tôi không thể tạo nội dung này";
        var refusalRes = ContentCandidateParser.ParseVideoContent(refusal, "project-1", "scene-1", 1, null);
        if (refusalRes.IsCompliant || refusalRes.Candidate == null || refusalRes.Candidate.VideoPrompt != string.Empty
            || refusalRes.RawText != refusal || refusalRes.Errors.Count == 0)
        {
            throw new InvalidOperationException("Kỳ vọng văn bản thường không có video_prompt hợp lệ, giữ rawText để người dùng sửa.");
        }

        Console.WriteLine("PASS");
    }

    /// <summary>
    /// C05: Số liệu null trong AnalyticsContext phải phân biệt rạch ròi với 0 thật.
    /// </summary>
    private static void TestAnalyticsContextMetricNullDistinctFromZero_C05()
    {
        Console.Write("[RUN] Test C05: Phân biệt số liệu null (thiếu) với 0 thật trong AnalyticsContext ... ");

        var metrics = new Dictionary<string, double?>
        {
            { "totalViews", 15200.0 },
            { "completionRate", null }, // Dữ liệu thiếu
            { "adRevenue", 0.0 }        // Doanh thu thực sự bằng 0
        };

        var context = new AnalyticsContextDto(
            SnapshotId: "snap-001",
            AccountId: "account-test",
            ChannelIdentity: "@testchannel",
            Source: "CSV_IMPORT",
            CapturedAtUtc: "2026-10-27T00:00:00Z",
            PeriodStartUtc: "2026-10-13T00:00:00Z",
            PeriodEndUtc: "2026-10-27T00:00:00Z",
            Timezone: "UTC+07",
            IsComplete: false,
            Metrics: metrics,
            VideoRecordIds: new List<string> { "VID-1", "VID-2" },
            SelectionManifestJson: null);

        // Serialization và deserialization JSON phải bảo toàn null khác 0
        string json = JsonSerializer.Serialize(context);
        var restored = JsonSerializer.Deserialize<AnalyticsContextDto>(json);

        if (restored == null)
        {
            throw new InvalidOperationException("Deserialization AnalyticsContextDto thất bại.");
        }

        if (restored.Metrics["completionRate"] is not null)
        {
            throw new InvalidOperationException("Kỳ vọng completionRate là null (dữ liệu thiếu), không được tự đổi thành 0.");
        }

        if (restored.Metrics["adRevenue"] != 0.0)
        {
            throw new InvalidOperationException("Kỳ vọng adRevenue là 0.0.");
        }

        Console.WriteLine("PASS");
    }

    /// <summary>
    /// C13: Bài đăng TikTok luôn ở phạm vi cấp project, không nhân bản theo từng cảnh.
    /// </summary>
    private static void TestTikTokPostProjectScope_C13()
    {
        Console.Write("[RUN] Test C13: Bài đăng TikTok thuộc phạm vi cấp Project ... ");

        var post = new TikTokPostDto(
            Caption: "Bí quyết quản lý thời gian buổi sáng hiệu quả #shorts #tips",
            Hashtags: new List<string> { "#shorts", "#tips" });

        var scenes = new List<VideoPromptSceneDto>
        {
            new("scene_1", "Prompt 1", null),
            new("scene_2", "Prompt 2", null),
            new("scene_3", "Prompt 3", null)
        };

        var output = new GenerateVideoPromptsOutputDto(scenes, post);

        if (output.Scenes.Count != 3)
        {
            throw new InvalidOperationException("Kỳ vọng 3 scenes.");
        }

        if (output.TikTokPost.Hashtags.Count != 2)
        {
            throw new InvalidOperationException("Kỳ vọng 2 hashtags.");
        }

        Console.WriteLine("PASS");
    }
}
