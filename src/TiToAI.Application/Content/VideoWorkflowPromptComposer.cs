using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TiToAI.Application.Prompts;
using TiToAI.Application.Prompts.Exceptions;
using TiToAI.Application.Prompts.Models;
using TiToAI.Contracts.Content;
using TiToAI.Contracts.Jobs;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Application.Content;

/// <summary>
/// Kết quả tổng hợp yêu cầu gửi AI writer (Prompt Composition Result) theo CLIP_DURATION_STORYBOARD_PLAN §3.1–3.3.
/// </summary>
public sealed record VideoPromptCompositionResult(
    string TemplateId,
    string TemplateName,
    string ComposedPrompt,
    int OutputContractVersion
);

/// <summary>
/// Bộ tổng hợp Prompt chuyên dụng cho quy trình sản xuất video từ ảnh.
/// Thực hiện quy tắc composition §3.1: Dùng template đã render -> Nhiệm vụ và ngữ cảnh cảnh -> OUTPUT_CONTRACT.
/// Bảo đảm tương thích template tùy chỉnh, không lặp render nội dung người dùng, báo lỗi rõ ràng nếu thiếu biến bắt buộc.
/// </summary>
public sealed class VideoWorkflowPromptComposer
{
    private readonly DbContextOptions<TiToAIDbContext> _dbOptions;

    public VideoWorkflowPromptComposer(DbContextOptions<TiToAIDbContext> dbOptions)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
    }

    /// <summary>
    /// Tổng hợp yêu cầu gửi AI writer viết prompt chuyển động cho một Cảnh (Clip).
    /// </summary>
    public async Task<VideoPromptCompositionResult> ComposeVideoPromptAiRequestAsync(
        ProjectWorkflowConfigDto config,
        Scene scene,
        CancellationToken cancellationToken = default,
        string? scenePurpose = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(scene);


        await using var dbContext = new TiToAIDbContext(_dbOptions);
        scenePurpose ??= await dbContext.AppSettings.AsNoTracking()
            .Where(setting => setting.Key == $"scene.plan.{scene.Id}")
            .Select(setting => setting.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var template = await dbContext.PromptTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Purpose == JobTaskType.VideoPrompt && t.IsActive, cancellationToken)
            .ConfigureAwait(false);

        string templateId = template?.Id ?? "default-video-prompt";
        string templateName = template != null ? $"Mẫu {template.Purpose} toàn cục" : "Mẫu Prompt Video Chuẩn";
        string templateText = template?.TemplateText ?? PromptTemplateService.DefaultVideoPromptInstruction;

        // Frontend renders dynamic fields before saving. Backend uses the resulting text.
        string renderedTemplate = ResolveRenderedTemplate(config.VideoPromptText, templateText, templateName);

        // 3. Ghép 4 phần canonical theo §3.1:
        // [HƯỚNG DẪN TỪ TEMPLATE VIDEO PROMPT ĐÃ CHỌN]
        // [NHIỆM VỤ]
        // [DỮ LIỆU LẦN TẠO]
        // [OUTPUT_CONTRACT]
        var sb = new StringBuilder();
        sb.AppendLine(renderedTemplate.Trim());
        sb.AppendLine();
        // Mô tả cảnh do bước Script tạo: dữ liệu của cảnh, không phải chỉ dẫn.
        if (!string.IsNullOrWhiteSpace(scenePurpose)) sb.AppendLine($"SCENE_PURPOSE: {scenePurpose}");
        sb.AppendLine("[OUTPUT_CONTRACT]");
        sb.AppendLine("Chỉ trả về DUY NHẤT một JSON hợp lệ, không markdown, theo schema: {\"video_prompt\": \"chuỗi prompt video\"}.");

        return new VideoPromptCompositionResult(
            TemplateId: templateId,
            TemplateName: templateName,
            ComposedPrompt: sb.ToString().TrimEnd(),
            OutputContractVersion: 1
        );
    }

    /// <summary>
    /// Tổng hợp yêu cầu gửi AI writer cho bước Script lập danh sách cảnh tự động.
    /// </summary>
    public async Task<VideoPromptCompositionResult> ComposeScriptPlanningAiRequestAsync(
        ProjectWorkflowConfigDto config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);


        await using var dbContext = new TiToAIDbContext(_dbOptions);

        var template = await dbContext.PromptTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Purpose == JobTaskType.Script && t.IsActive, cancellationToken)
            .ConfigureAwait(false);

        string templateId = template?.Id ?? "default-script";
        string templateName = template != null ? $"Mẫu {template.Purpose} toàn cục" : "Mẫu Kịch Bản Chuẩn";
        string templateText = template?.TemplateText ?? PromptTemplateService.DefaultScriptInstruction;

        string renderedTemplate = ResolveRenderedTemplate(config.ScriptPromptText, templateText, templateName);

        var sb = new StringBuilder();
        sb.AppendLine("[HƯỚNG DẪN TỪ TEMPLATE KỊCH BẢN ĐÃ CHỌN]");
        sb.AppendLine(renderedTemplate.Trim());
        sb.AppendLine();
        sb.AppendLine("[NHIỆM VỤ]");
        sb.AppendLine($"Lập storyboard cho đúng một video dài {config.TargetDurationPerSceneSeconds} giây cho mục tiêu được cung cấp. Chia video thành các phân cảnh liên tiếp, bắt đầu từ 0 và kết thúc đúng {config.TargetDurationPerSceneSeconds} giây; mỗi phân cảnh có mô tả hành động và prompt ảnh tĩnh riêng.");
        sb.AppendLine();
        sb.AppendLine("[DỮ LIỆU LẦN TẠO — do app serialize]");
        sb.AppendLine($"TOTAL_DURATION: {config.TargetDurationPerSceneSeconds} giây.");
        sb.AppendLine($"WORKFLOW_MODE: {config.WorkflowMode}.");
        sb.AppendLine();
        sb.AppendLine("[OUTPUT_CONTRACT]");
        sb.AppendLine($"Chỉ trả về JSON hợp lệ, không markdown. Schema: {{\"durationSeconds\":{config.TargetDurationPerSceneSeconds},\"shots\":[{{\"startSeconds\":0,\"endSeconds\":5,\"description\":\"mô tả hành động\",\"imagePrompt\":\"prompt ảnh tĩnh của phân cảnh\"}}]}}. Các shot liên tiếp, shot đầu bắt đầu từ 0, endSeconds của shot cuối bằng durationSeconds, tối đa 30 shot.");

        return new VideoPromptCompositionResult(
            TemplateId: templateId,
            TemplateName: templateName,
            ComposedPrompt: sb.ToString().TrimEnd(),
            OutputContractVersion: 1
        );
    }

    /// <summary>
    /// Tổng hợp yêu cầu gửi AI writer viết content và prompt video theo schema:
    /// { idea, hook, video_script, video_prompt, tiktok_content }.
    /// </summary>
    public async Task<VideoPromptCompositionResult> ComposeContentAndPromptAiRequestAsync(
        ProjectWorkflowConfigDto config,
        Scene? scene = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sb = new StringBuilder();
        sb.AppendLine("[VAI TRÒ]");
        sb.AppendLine("Bạn là chuyên gia biên kịch video ngắn (TikTok/Reels/Shorts) và chuyên gia tạo prompt AI video chuyên nghiệp.");
        sb.AppendLine();
        sb.AppendLine("[HƯỚNG DẪN NỘI DUNG]");
        sb.AppendLine(config.ScriptPromptText ?? string.Empty);
        sb.AppendLine($"WORKFLOW_MODE: {config.WorkflowMode}");
        sb.AppendLine($"DURATION: {config.TargetDurationPerSceneSeconds} giây");
        if (scene != null)
        {
            sb.AppendLine($"SCENE_ORDER: {scene.SequenceOrder}");
            sb.AppendLine($"SCENE_NAME: {scene.Name}");
        }
        sb.AppendLine();
        sb.AppendLine("[QUY CHUẨN ĐẦU RA - JSON BẮT BUỘC]");
        sb.AppendLine("Hãy trả về DUY NHẤT một khối mã JSON hợp lệ theo đúng schema sau:");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"idea\": \"Ý tưởng video cốt lõi ngắn gọn và hấp dẫn\",");
        sb.AppendLine("  \"hook\": \"Câu mở đầu gây tò mò giữ chân người xem trong 3 giây đầu tiên\",");
        sb.AppendLine("  \"video_script\": \"Kịch bản chi tiết từng phân cảnh (mô tả hành động, lời thoại/lồng tiếng, chuyển cảnh)\",");
        sb.AppendLine("  \"video_prompt\": \"Prompt tiếng Anh chi tiết cho mô hình AI video (chuyển động camera, góc máy, ánh sáng, chi tiết trang phục/sản phẩm 4K photorealistic)\",");
        sb.AppendLine("  \"tiktok_content\": \"Tiêu đề video cuốn hút, caption đầy đủ kêu gọi hành động kèm 3-5 hashtag thịnh hành\"");
        sb.AppendLine("}");
        sb.AppendLine("```");

        return new VideoPromptCompositionResult(
            TemplateId: "content-schema-v1",
            TemplateName: "Schema Kịch Bản & Prompt Video",
            ComposedPrompt: sb.ToString().TrimEnd(),
            OutputContractVersion: 1
        );
    }

    private static string ResolveRenderedTemplate(string? renderedPromptText, string templateText, string templateName)
    {
        var promptText = string.IsNullOrWhiteSpace(renderedPromptText) ? templateText : renderedPromptText.Trim();
        var (detectedVariables, malformed) = PromptRenderer.ParseTemplate(promptText);
        if (malformed.Count > 0)
        {
            throw new ProjectWorkflowValidationException(
                $"Template '{templateName}' chứa placeholder lỗi cú pháp: {string.Join(", ", malformed)}");
        }

        if (detectedVariables.Count > 0)
        {
            throw new ProjectWorkflowValidationException(
                $"Prompt '{templateName}' còn biến chưa được điền: {string.Join(", ", detectedVariables)}. Vui lòng điền biến trong giao diện rồi lưu lại.");
        }

        return promptText;
    }

}
