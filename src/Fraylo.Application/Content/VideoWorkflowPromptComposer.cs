using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Prompts;
using Fraylo.Application.Prompts.Exceptions;
using Fraylo.Application.Prompts.Models;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Content;

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
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;

    public VideoWorkflowPromptComposer(DbContextOptions<FrayloDbContext> dbOptions)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
    }

    /// <summary>Mục đích prompt ghép người mẫu với sản phẩm thành một ảnh (chế độ Mẫu + sản phẩm).</summary>
    public const string ComposeImagePurpose = "ComposeImage";

    /// <summary>Mẫu prompt ảnh dùng cho job Image của chế độ đang chạy: Mẫu + sản phẩm dùng mẫu Ghép ảnh.</summary>
    public static string ImagePurposeFor(string workflowMode) =>
        workflowMode == WorkflowModes.ModelAndProduct ? ComposeImagePurpose : JobTaskType.Image;

    /// <summary>Run tạo một ảnh storyboard 9 ô rồi render một video từ đúng ảnh đó.</summary>
    public static bool IsGridMode(ProjectWorkflowConfigDto config) =>
        config.VideoStrategy == VideoStrategies.SingleVideo && config.ReferenceImageMode == ReferenceImageModes.StoryboardGrid9;

    /// <summary>Hướng dẫn bắt buộc cho prompt video khi ảnh tham chiếu là storyboard 3×3 (STORYBOARD_CONTACT_SHEET §3.5).</summary>
    public const string GridVideoInstruction =
        "GRID_REFERENCE: Ảnh tham chiếu là storyboard 3×3, không phải bố cục video đầu ra. Đọc trái sang phải, trên xuống dưới. " +
        "Mỗi ô thành một cảnh toàn màn hình theo mốc thời gian đã cho. Tạo một video dọc 9:16 dài 15 giây. " +
        "Không hiển thị bảng, viền hoặc số ô; không chia màn hình. Cắt dựng giữa các cảnh, không morph trang phục hay trộn các ô. " +
        "Giữ đúng sản phẩm và cùng người mẫu. Chỉ thực hiện chuyển động được mô tả trong từng cảnh.";

    /// <summary>
    /// Prompt cho job Image tạo một ảnh storyboard 3×3 từ 9 shot: nội dung mẫu Image đang chọn (đã render) rồi phần bố cục bắt buộc.
    /// Chỉ có một ảnh được sinh; 9 shot chỉ là mô tả của 9 ô.
    /// </summary>
    public static string ComposeGridImagePrompt(string renderedImageTemplate, StoryboardContent storyboard)
    {
        var sb = new StringBuilder();
        sb.AppendLine(renderedImageTemplate.Trim());
        sb.AppendLine();
        sb.AppendLine("[BỐ CỤC STORYBOARD]");
        sb.AppendLine("Tạo MỘT ảnh duy nhất gồm đúng 3 cột × 3 hàng (9 ô), toàn ảnh dọc 9:16, mỗi ô dọc 9:16, viền mảnh giữa các ô. " +
                      "Ảnh đính kèm là ảnh nguồn: giữ đúng màu sắc, hình dáng, đường may, họa tiết và phụ kiện của sản phẩm; cùng nền, ánh sáng và cùng người mẫu ở mọi ô, chỉ khác góc quay. " +
                      "Không đặt chữ hoặc số vào ảnh. Không thêm giá, giảm giá, chứng nhận, công dụng hay biến thể màu không có trong ảnh nguồn.");
        sb.AppendLine("Thứ tự đọc trái sang phải, trên xuống dưới. Nội dung từng ô:");
        for (var i = 0; i < storyboard.Shots.Count; i++)
        {
            var shot = storyboard.Shots[i];
            sb.AppendLine($"Ô {i + 1} ({shot.StartSeconds}–{shot.EndSeconds} giây): {shot.ImagePrompt.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Prompt chung là bắt buộc: backend không tự dùng prompt dựng sẵn, chưa chọn mẫu có nội dung thì từ chối chạy.
    /// </summary>
    public static async Task<PromptTemplate> RequireActiveTemplateAsync(
        FrayloDbContext dbContext, string purpose, CancellationToken cancellationToken)
    {
        var template = await dbContext.PromptTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Purpose == purpose && t.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (template == null || string.IsNullOrWhiteSpace(template.TemplateText))
        {
            throw new ProjectWorkflowValidationException(
                $"Chưa có Prompt {PurposeLabel(purpose)} toàn ứng dụng. Vào trang Prompt, tab {PurposeLabel(purpose)}: tạo mẫu có nội dung, lưu và bấm \"Chọn sử dụng\" trước khi chạy.");
        }
        return template;
    }

    private static string PurposeLabel(string purpose) => purpose switch
    {
        ComposeImagePurpose => "Ghép ảnh",
        JobTaskType.VideoPrompt => "Video Prompt",
        _ => purpose
    };

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


        await using var dbContext = new FrayloDbContext(_dbOptions);
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
        if (IsGridMode(config)) sb.AppendLine(GridVideoInstruction);
        await AppendTikTokContentGuidanceAsync(sb, dbContext, cancellationToken).ConfigureAwait(false);
        sb.AppendLine("[OUTPUT_CONTRACT]");
        sb.AppendLine("Chỉ trả về DUY NHẤT một JSON hợp lệ, không markdown, theo schema: {\"video_prompt\": \"chuỗi prompt video\", \"tiktok_content\": \"nội dung đăng TikTok cho video: tiêu đề cuốn hút, caption kêu gọi hành động kèm 3-5 hashtag thịnh hành\"}.");

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


        await using var dbContext = new FrayloDbContext(_dbOptions);

        var prompt = await ResolvePromptAsync(dbContext, config, JobTaskType.Script, cancellationToken).ConfigureAwait(false);
        string templateId = prompt.Id;
        string templateName = prompt.Name;
        string renderedTemplate = prompt.Text;

        var sb = new StringBuilder();
        sb.AppendLine(renderedTemplate.Trim());
        sb.AppendLine();
        bool singleVideo = config.VideoStrategy == VideoStrategies.SingleVideo;
        if (!singleVideo) await AppendTikTokContentGuidanceAsync(sb, dbContext, cancellationToken).ConfigureAwait(false);
        sb.AppendLine("[OUTPUT_CONTRACT]");
        if (IsGridMode(config))
        {
            sb.AppendLine($"Chỉ trả về JSON hợp lệ, không markdown. Schema: {{\"durationSeconds\":{ReferenceImageModes.GridDurationSeconds},\"shots\":[{{\"startSeconds\":0,\"endSeconds\":2,\"description\":\"mô tả hành động và nội dung của cảnh\",\"imagePrompt\":\"mô tả hình của ô tương ứng trong ảnh storyboard\",\"motionPrompt\":\"một chuyển động camera hoặc chủ thể rõ ràng của cảnh\"}}]}}. Đúng {ReferenceImageModes.GridShotCount} shot theo thứ tự đọc trái sang phải, trên xuống dưới; shot đầu bắt đầu từ 0, các shot liên tiếp không hở không chồng, số giây nguyên, endSeconds của shot cuối bằng {ReferenceImageModes.GridDurationSeconds}. Không thêm giá, giảm giá, chứng nhận, công dụng hay biến thể sản phẩm không có trong dữ liệu đầu vào.");
        }
        else if (singleVideo)
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

    /// <summary>Purpose của mẫu prompt toàn cục quy định cách viết nội dung đăng TikTok. Tùy chọn: chưa chọn thì dùng mô tả mặc định trong OUTPUT_CONTRACT.</summary>
    public const string TikTokContentPurpose = "TikTokContent";

    private static async Task AppendTikTokContentGuidanceAsync(
        StringBuilder sb, FrayloDbContext dbContext, CancellationToken cancellationToken)
    {
        var text = await dbContext.PromptTemplates.AsNoTracking()
            .Where(t => t.Purpose == TikTokContentPurpose && t.IsActive)
            .Select(t => t.TemplateText).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.AppendLine("[QUY TẮC VIẾT NỘI DUNG ĐĂNG TIKTOK]");
        sb.AppendLine("Áp dụng cho trường tiktokContent/tiktok_content (ưu tiên hơn mô tả trường trong OUTPUT_CONTRACT):");
        sb.AppendLine(text.Trim());
        sb.AppendLine();
    }

    /// <summary>
    /// Chọn prompt theo mức ưu tiên: Dự án (nếu có) &gt; Nhóm (nếu có) &gt; mẫu toàn cục đang chọn. Chỉ dùng đúng một mức,
    /// không ghép, không điền biến. Chỉ khi cả Dự án lẫn Nhóm để trống mới bắt buộc có mẫu toàn cục.
    /// </summary>
    public static async Task<ResolvedPrompt> ResolvePromptAsync(
        FrayloDbContext dbContext,
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
