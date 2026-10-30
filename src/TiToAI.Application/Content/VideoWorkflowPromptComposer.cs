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
/// Prompt đã chọn cho một tác vụ: nguồn (Dự án / Nhóm / mẫu toàn cục) và nội dung nguyên văn.
/// </summary>
public sealed record ResolvedPrompt(string Id, string Name, string Text);

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
    /// Prompt chung là bắt buộc: backend không tự dùng prompt dựng sẵn, chưa chọn mẫu có nội dung thì từ chối chạy.
    /// </summary>
    public static async Task<PromptTemplate> RequireActiveTemplateAsync(
        TiToAIDbContext dbContext, string purpose, CancellationToken cancellationToken)
    {
        var template = await dbContext.PromptTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Purpose == purpose && t.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (template == null || string.IsNullOrWhiteSpace(template.TemplateText))
        {
            throw new ProjectWorkflowValidationException(
                $"Chưa có Prompt {purpose} toàn ứng dụng. Vào trang Prompt, tab {purpose}: tạo mẫu có nội dung, lưu và bấm \"Chọn sử dụng\" trước khi chạy.");
        }
        return template;
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

        var prompt = await ResolvePromptAsync(dbContext, config, JobTaskType.VideoPrompt, cancellationToken).ConfigureAwait(false);
        string templateId = prompt.Id;
        string templateName = prompt.Name;
        string renderedTemplate = prompt.Text;

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

        var prompt = await ResolvePromptAsync(dbContext, config, JobTaskType.Script, cancellationToken).ConfigureAwait(false);
        string templateId = prompt.Id;
        string templateName = prompt.Name;
        string renderedTemplate = prompt.Text;

        var sb = new StringBuilder();
        sb.AppendLine(renderedTemplate.Trim());
        sb.AppendLine();
        bool singleVideo = config.VideoStrategy == VideoStrategies.SingleVideo;
        sb.AppendLine("[OUTPUT_CONTRACT]");
        if (singleVideo)
        {
            sb.AppendLine($"Chỉ trả về JSON hợp lệ, không markdown. Schema: {{\"durationSeconds\":{config.TargetDurationPerSceneSeconds},\"shots\":[{{\"startSeconds\":0,\"endSeconds\":5,\"description\":\"mô tả hành động của đoạn\"}}]}}. Các shot liên tiếp, shot đầu bắt đầu từ 0, endSeconds của shot cuối bằng durationSeconds, tối đa 30 shot.");
        }
        else
        {
            sb.AppendLine($"Chỉ trả về JSON hợp lệ, không markdown. Schema: {{\"scenes\":[{{\"name\":\"tên cảnh\",\"imagePrompt\":\"prompt ảnh tĩnh của cảnh\",\"videoPrompt\":\"prompt video chuyển động của cảnh, dài {config.TargetDurationPerSceneSeconds} giây\",\"tiktokContent\":\"nội dung đăng TikTok cho cảnh: tiêu đề cuốn hút, caption kêu gọi hành động kèm 3-5 hashtag thịnh hành\"}}]}}. Trả đúng {config.TargetSceneCount} cảnh.");
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
    /// Chọn prompt theo mức ưu tiên: Dự án (nếu có) &gt; Nhóm (nếu có) &gt; mẫu toàn cục đang chọn. Chỉ dùng đúng một mức,
    /// không ghép, không điền biến. Chỉ khi cả Dự án lẫn Nhóm để trống mới bắt buộc có mẫu toàn cục.
    /// </summary>
    public static async Task<ResolvedPrompt> ResolvePromptAsync(
        TiToAIDbContext dbContext,
        ProjectWorkflowConfigDto config,
        string purpose,
        CancellationToken cancellationToken)
    {
        bool isVideoPrompt = purpose == JobTaskType.VideoPrompt;
        var projectPrompt = isVideoPrompt ? config.VideoGuidance : config.ScriptGuidance;
        if (!string.IsNullOrWhiteSpace(projectPrompt))
        {
            return new ResolvedPrompt("project", "Prompt của dự án", projectPrompt.Trim());
        }

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
        var groupPrompt = isVideoPrompt ? group?.VideoGuidance : group?.ScriptGuidance;
        if (group is not null && !string.IsNullOrWhiteSpace(groupPrompt))
        {
            return new ResolvedPrompt($"group:{group.Id}", $"Prompt của nhóm {group.Name}", groupPrompt.Trim());
        }

        var template = await RequireActiveTemplateAsync(dbContext, purpose, cancellationToken).ConfigureAwait(false);
        return new ResolvedPrompt(template.Id, $"Mẫu {template.Purpose} toàn cục", template.TemplateText);
    }

}
