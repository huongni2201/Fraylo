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
        string renderedTemplate = await ResolveInstructionAsync(dbContext, config, templateText, templateName, isVideoPrompt: true, cancellationToken).ConfigureAwait(false);

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

        string renderedTemplate = await ResolveInstructionAsync(dbContext, config, templateText, templateName, isVideoPrompt: false, cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("[HƯỚNG DẪN TỪ TEMPLATE KỊCH BẢN ĐÃ CHỌN]");
        sb.AppendLine(renderedTemplate.Trim());
        sb.AppendLine();
        bool singleVideo = config.VideoStrategy == VideoStrategies.SingleVideo;
        sb.AppendLine("[NHIỆM VỤ]");
        if (singleVideo)
        {
            sb.AppendLine($"Lập storyboard cho đúng một video dài {config.TargetDurationPerSceneSeconds} giây cho mục tiêu được cung cấp. Chia video thành các đoạn liên tiếp, bắt đầu từ 0 và kết thúc tại thời lượng video; mỗi đoạn có mô tả hành động.");
        }
        else
        {
            sb.AppendLine($"Lập đúng {config.TargetSceneCount} cảnh, mỗi cảnh là một clip riêng dài {config.TargetDurationPerSceneSeconds} giây cho mục tiêu được cung cấp. Mỗi cảnh có prompt ảnh tĩnh và prompt video chuyển động riêng, khác nhau giữa các cảnh.");
        }
        sb.AppendLine();
        sb.AppendLine("[DỮ LIỆU LẦN TẠO — do app serialize]");
        sb.AppendLine($"TOTAL_DURATION: {config.TargetDurationPerSceneSeconds} giây.");
        sb.AppendLine($"WORKFLOW_MODE: {config.WorkflowMode}.");
        sb.AppendLine($"VIDEO_STRATEGY: {config.VideoStrategy}.");
        sb.AppendLine();
        sb.AppendLine("[OUTPUT_CONTRACT]");
        if (singleVideo)
        {
            sb.AppendLine($"Chỉ trả về JSON hợp lệ, không markdown. Schema: {{\"durationSeconds\":{config.TargetDurationPerSceneSeconds},\"shots\":[{{\"startSeconds\":0,\"endSeconds\":5,\"description\":\"mô tả hành động của đoạn\"}}]}}. Các shot liên tiếp, shot đầu bắt đầu từ 0, endSeconds của shot cuối bằng durationSeconds, tối đa 30 shot.");
        }
        else
        {
            sb.AppendLine($"Chỉ trả về JSON hợp lệ, không markdown. Schema: {{\"scenes\":[{{\"name\":\"tên cảnh\",\"imagePrompt\":\"prompt ảnh tĩnh của cảnh\",\"videoPrompt\":\"prompt video chuyển động của cảnh, dài {config.TargetDurationPerSceneSeconds} giây\"}}]}}. Trả đúng {config.TargetSceneCount} cảnh.");
        }

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
        sb.AppendLine(config.ScriptGuidance ?? string.Empty);
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

    /// <summary>
    /// Điền biến vào mẫu chung rồi nối hướng dẫn của Nhóm và Dự án ở cuối. Biến lấy theo thứ tự: mặc định dựng sẵn,
    /// biến Nhóm, biến Dự án (Dự án ghi đè Nhóm).
    /// </summary>
    private static async Task<string> ResolveInstructionAsync(
        TiToAIDbContext dbContext,
        ProjectWorkflowConfigDto config,
        string templateText,
        string templateName,
        bool isVideoPrompt,
        CancellationToken cancellationToken)
    {
        var groupId = await dbContext.Projects
            .AsNoTracking()
            .Where(p => p.Id == config.ProjectId)
            .Select(p => p.GroupId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var group = groupId is null ? null : await dbContext.ProjectGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken)
            .ConfigureAwait(false);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in PromptGuidance.ParseVariablesJson(group?.PromptVariablesJson)) variables[pair.Key] = pair.Value;
        foreach (var pair in config.PromptVariables ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(pair.Value)) variables[pair.Key] = pair.Value;
        }
        // Biến hệ thống luôn theo thời lượng cấu hình, không bị nhóm hay dự án ghi đè.
        var duration = $"{config.TargetDurationPerSceneSeconds}s";
        foreach (var name in new[] { "duration", "DURATION", "thoi_luong", "THOI_LUONG" }) variables[name] = duration;

        var (detectedVariables, malformed) = PromptRenderer.ParseTemplate(templateText);
        if (malformed.Count > 0)
        {
            throw new ProjectWorkflowValidationException(
                $"Template '{templateName}' chứa placeholder lỗi cú pháp: {string.Join(", ", malformed)}");
        }

        var missing = detectedVariables.Where(name => !variables.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            throw new ProjectWorkflowValidationException(
                $"Prompt '{templateName}' còn biến chưa được điền: {string.Join(", ", missing)}. Vui lòng điền biến trong Nhóm hoặc Dự án rồi lưu lại.");
        }

        var sb = new StringBuilder(PromptRenderer.Render(templateText, variables).Trim());
        var groupGuidance = isVideoPrompt ? group?.VideoGuidance : group?.ScriptGuidance;
        var projectGuidance = isVideoPrompt ? config.VideoGuidance : config.ScriptGuidance;
        if (!string.IsNullOrWhiteSpace(groupGuidance))
        {
            sb.AppendLine().AppendLine().AppendLine("[HƯỚNG DẪN BỔ SUNG CỦA NHÓM]").Append(groupGuidance.Trim());
        }
        if (!string.IsNullOrWhiteSpace(projectGuidance))
        {
            sb.AppendLine().AppendLine().AppendLine("[HƯỚNG DẪN BỔ SUNG CỦA DỰ ÁN]").Append(projectGuidance.Trim());
        }

        return sb.ToString();
    }

}
