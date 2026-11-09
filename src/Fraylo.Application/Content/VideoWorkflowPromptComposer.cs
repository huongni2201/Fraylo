using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Prompts;
using Fraylo.Application.Prompts.Exceptions;
using Fraylo.Application.Prompts.Models;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Prompts;
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
/// Prompt đã chọn cho một tác vụ: nguồn (Dự án / mẫu toàn cục) và nội dung nguyên văn.
/// </summary>
public sealed record ResolvedPrompt(string Id, string Name, string Text);

/// <summary>
/// Bộ tổng hợp Prompt chuyên dụng cho quy trình sản xuất video từ ảnh.
/// Thực hiện quy tắc composition §3.1: Dùng template đã render -> Nhiệm vụ và ngữ cảnh cảnh -> OUTPUT_CONTRACT.
/// Bảo đảm tương thích template tùy chỉnh, không lặp render nội dung người dùng, báo lỗi rõ ràng nếu thiếu biến bắt buộc.
/// </summary>
public sealed class VideoWorkflowPromptComposer
{
    private sealed record BuiltInPrompt(string Name, string TemplateText);
    private static readonly IReadOnlyDictionary<string, BuiltInPrompt> BuiltInPrompts = LoadBuiltInPrompts();
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;

    public VideoWorkflowPromptComposer(DbContextOptions<FrayloDbContext> dbOptions)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
    }

    /// <summary>Mục đích prompt ghép người mẫu với sản phẩm thành một ảnh (chế độ Mẫu + sản phẩm).</summary>
    public const string ComposeImagePurpose = "ComposeImage";

    private static IReadOnlyDictionary<string, BuiltInPrompt> LoadBuiltInPrompts()
    {
        using var resource = typeof(VideoWorkflowPromptComposer).Assembly.GetManifestResourceStream("Fraylo.TikTokPromptDefaults.json")
            ?? throw new InvalidOperationException("Thiếu tài nguyên prompt mặc định của TikTok.");
        return JsonSerializer.Deserialize<Dictionary<string, BuiltInPrompt>>(resource, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Tài nguyên prompt mặc định của TikTok rỗng.");
    }

    /// <summary>Run tạo một ảnh storyboard rồi render một video từ đúng ảnh đó.</summary>
    public static bool IsGridMode(ProjectWorkflowConfigDto config) =>
        config.VideoStrategy == VideoStrategies.SingleVideo && ReferenceImageModes.GridSize(config.ReferenceImageMode) > 0;

    private static string GridVideoInstruction(int durationSeconds, int gridSize) => gridSize == 5
        ? $"""
            GRID_REFERENCE: Dùng ảnh 5×5 làm 25 khung hình mốc của một video dọc 9:16 dài đúng {durationSeconds} giây.
            Đọc từ trái sang phải, trên xuống dưới. Bám thứ tự và mốc thời gian trong SCENE_PURPOSE.
            Nối các khung liên tiếp cùng hành động thành chuyển động toàn màn hình liền mạch. Chỉ cắt khi có đổi góc máy hoặc chuyển đoạn có chủ đích.
            Giữ đúng người mẫu, sản phẩm, trang phục, màu sắc và chi tiết nhìn thấy. Bỏ viền, ô lưới và số ô khỏi video. Không trộn hai ô hay biến dạng sản phẩm.
            """
        : "GRID_REFERENCE: Ảnh tham chiếu là storyboard 3×3, không phải bố cục video đầu ra. Đọc trái sang phải, trên xuống dưới. " +
        $"Mỗi ô thành một cảnh toàn màn hình theo mốc thời gian đã cho. Tạo một video dọc 9:16 dài {durationSeconds} giây. " +
        "Không hiển thị bảng, viền hoặc số ô; không chia màn hình. Cắt dựng giữa các cảnh, không morph trang phục hay trộn các ô. " +
        "Giữ đúng sản phẩm và cùng người mẫu. Chỉ thực hiện chuyển động được mô tả trong từng cảnh.";

    /// <summary>
    /// Prompt tạo một ảnh storyboard, với mỗi shot mô tả một ô.
    /// </summary>
    public static string ComposeGridImagePrompt(string renderedImageTemplate, StoryboardContent storyboard, int gridSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine(renderedImageTemplate.Trim());
        sb.AppendLine();
        sb.AppendLine("[BỐ CỤC STORYBOARD]");
        var framing = gridSize == 5 ? "Góc máy và tư thế thay đổi theo mô tả từng ô." : "Chỉ thay đổi góc máy giữa các ô.";
        sb.AppendLine($"Tạo MỘT ảnh duy nhất gồm đúng {gridSize} cột × {gridSize} hàng ({storyboard.Shots.Count} ô), toàn ảnh dọc 9:16, mỗi ô dọc 9:16, viền mảnh giữa các ô. " +
                      "Ảnh đính kèm là ảnh nguồn: giữ đúng màu sắc, hình dáng, đường may, họa tiết và phụ kiện của sản phẩm; cùng nền, ánh sáng và cùng người mẫu ở mọi ô. " +
                      framing + " " +
                      "Không đặt chữ hoặc số vào ảnh. Không thêm giá, giảm giá, chứng nhận, công dụng hay biến thể màu không có trong ảnh nguồn.");
        if (gridSize == 5)
        {
            sb.AppendLine("Ưu tiên bố cục 5×5 khi mẫu phong cách mô tả ảnh đơn. Các ô cùng kích thước, không gộp hoặc bỏ ô. " +
                          "Ảnh đính kèm quyết định nhận diện người mẫu và chi tiết sản phẩm. Mỗi ô là một ảnh tĩnh thể hiện đúng góc máy, tư thế và hành động đã mô tả.");
        }
        sb.AppendLine("Thứ tự đọc trái sang phải, trên xuống dưới. Nội dung từng ô:");
        for (var i = 0; i < storyboard.Shots.Count; i++)
        {
            var shot = storyboard.Shots[i];
            sb.AppendLine(FormattableString.Invariant($"Ô {i + 1} ({shot.StartSeconds}–{shot.EndSeconds} giây): {shot.ImagePrompt.Trim()}"));
        }
        return sb.ToString().TrimEnd();
    }

    internal static async Task<string> RenderImageTemplateAsync(
        FrayloDbContext dbContext, string purpose, CancellationToken cancellationToken)
    {
        var template = await FindActiveTemplateAsync(dbContext, purpose, cancellationToken).ConfigureAwait(false);
        if (template == null) return BuiltInPrompts[purpose].TemplateText;
        var text = RemoveLegacyTopicPlaceholder(template.TemplateText);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var preview = await new PromptTemplateService(dbContext).PreviewAsync(
            new PromptPreviewRequest(template.Id, null, text, null), cancellationToken).ConfigureAwait(false);
        return preview.RenderedText;
    }

    // Giữ ngoặc escape trong mẫu khi gỡ biến chủ đề cũ.
    private static string RemoveLegacyTopicPlaceholder(string text) =>
        Regex.Replace(text, @"\{\{|\}\}|\{\s*chu_de\s*\}", match => match.Length == 2 ? match.Value : string.Empty);

    private static Task<PromptTemplate?> FindActiveTemplateAsync(
        FrayloDbContext dbContext, string purpose, CancellationToken cancellationToken) =>
        dbContext.PromptTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Purpose == purpose && t.IsActive && t.TemplateText.Trim() != "", cancellationToken);

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
        bool compactGrid = IsGridMode(config) && config.ReferenceImageMode == ReferenceImageModes.StoryboardGrid25;
        var renderedTemplate = compactGrid ? RemoveLegacyTopicPlaceholder(prompt.Text) : prompt.Text;

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
        if (IsGridMode(config))
            sb.AppendLine(GridVideoInstruction(config.TargetDurationPerSceneSeconds, ReferenceImageModes.GridSize(config.ReferenceImageMode)));
        await AppendTikTokContentGuidanceAsync(sb, dbContext, cancellationToken).ConfigureAwait(false);
        sb.AppendLine("[OUTPUT_CONTRACT]");
        if (compactGrid)
        {
            sb.AppendLine("""
                Trả đúng một đối tượng JSON hợp lệ với hai trường kiểu chuỗi, không dùng Markdown.
                {"video_prompt":"English render instructions","tiktok_content":"Caption TikTok theo quy tắc ở trên"}
                video_prompt: Mở đầu bằng yêu cầu về thời lượng, khung dọc và ảnh 5×5. Sau đó viết chỉ dẫn theo từng khoảng thời gian, giữ thứ tự các ô và gộp các ô liền kề cùng hành động. Mỗi khoảng nêu hành động, cỡ cảnh, góc máy và chuyển động máy quay cụ thể. Nêu hướng và chất lượng ánh sáng ở phần chỉ dẫn chung, giữ nhất quán giữa các khoảng. Kết thúc bằng ràng buộc nhận diện sản phẩm và khung hình toàn màn hình.
                """);
        }
        else
        {
            sb.AppendLine("Chỉ trả một đối tượng JSON hợp lệ, không Markdown: {\"video_prompt\":\"English render instructions\",\"tiktok_content\":\"Caption TikTok theo quy tắc ở trên\"}.");
        }

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
        bool compactGrid = IsGridMode(config) && config.ReferenceImageMode == ReferenceImageModes.StoryboardGrid25;
        var renderedTemplate = compactGrid ? RemoveLegacyTopicPlaceholder(prompt.Text) : prompt.Text;

        var sb = new StringBuilder();
        sb.AppendLine(renderedTemplate.Trim());
        sb.AppendLine();
        bool singleVideo = config.VideoStrategy == VideoStrategies.SingleVideo;
        if (!singleVideo)
        {
            var videoPrompt = await ResolvePromptAsync(dbContext, config, JobTaskType.VideoPrompt, cancellationToken).ConfigureAwait(false);
            sb.AppendLine("[HƯỚNG DẪN CHO videoPrompt CỦA TỪNG CẢNH]");
            sb.AppendLine(videoPrompt.Text.Trim());
            sb.AppendLine();
            await AppendTikTokContentGuidanceAsync(sb, dbContext, cancellationToken).ConfigureAwait(false);
        }
        sb.AppendLine("[OUTPUT_CONTRACT]");
        if (!compactGrid)
        {
            sb.AppendLine("BẮT BUỘC: toàn bộ phản hồi chỉ là MỘT đối tượng JSON, ký tự đầu tiên là { và ký tự cuối cùng là }. Không lời chào, không giải thích, không hỏi lại, không từ chối, không bọc markdown. Nếu thiếu dữ liệu thì tự suy luận hợp lý từ dữ liệu đã có và vẫn trả JSON đúng schema.");
        }
        if (compactGrid)
        {
            var reference = config.WorkflowMode == WorkflowModes.ModelAndProduct
                ? "Ảnh thứ nhất xác định người mẫu. Ảnh thứ hai xác định sản phẩm người mẫu sẽ giới thiệu."
                : "Ảnh đính kèm xác định người mẫu và sản phẩm xuất hiện trong video.";
            sb.AppendLine($$"""
                Lập storyboard 5×5 cho một video dọc {{config.TargetDurationPerSceneSeconds}} giây. Tạo đúng 25 shot, mỗi shot ứng với một ô. Thứ tự mảng là trái sang phải, trên xuống dưới.
                Mở bằng hình ảnh có sản phẩm rõ ràng, tiếp theo cho thấy cách sử dụng hoặc chi tiết nhìn thấy, kết bằng một khung sản phẩm dễ nhận diện. Dùng các ô liền kề để diễn tiến một hành động qua các tư thế nhỏ, tránh nhảy góc máy ở mọi ô.
                {{reference}} Giữ đúng màu, hình dáng và chi tiết thấy được, với ánh sáng và bối cảnh nhất quán. Bỏ qua thông tin chưa có căn cứ như giá, ưu đãi, chứng nhận, công dụng hoặc biến thể.
                Trả đúng một đối tượng JSON hợp lệ, không Markdown hay lời dẫn. Hợp đồng này quyết định cấu trúc và số shot.
                {"schemaVersion":{{StoryboardContentService.CompactGridSchemaVersion}},"shots":[{"description":"Hành động ngắn bằng tiếng Việt","imagePrompt":"Medium shot, model holds the product at chest height","motionPrompt":"Slow camera push-in toward the product"}]}
                Mỗi shot có đủ ba chuỗi ngắn. description ghi diễn tiến bằng tiếng Việt. imagePrompt viết bằng tiếng Anh, mô tả một khung tĩnh với góc máy, cỡ cảnh và tư thế cụ thể. motionPrompt viết bằng tiếng Anh, mô tả một chuyển động khả thi của chủ thể hoặc máy quay. Ở ô cuối, giữ tư thế kết.
                Mở rộng mảng ví dụ thành đủ 25 phần tử. Chỉ dùng các trường trong ví dụ. Fraylo sẽ tính mốc thời gian từ thứ tự mảng.
                """);
        }
        else if (IsGridMode(config))
        {
            sb.AppendLine($"Chỉ trả về một đối tượng JSON hợp lệ, không Markdown. Schema: {{\"durationSeconds\":{config.TargetDurationPerSceneSeconds},\"shots\":[{{\"startSeconds\":0,\"endSeconds\":2,\"description\":\"mô tả hành động và nội dung của cảnh\",\"imagePrompt\":\"mô tả hình của ô tương ứng trong ảnh storyboard\",\"motionPrompt\":\"một chuyển động camera hoặc chủ thể rõ ràng của cảnh\"}}]}}. Đúng 9 shot cho lưới 3×3 theo thứ tự đọc trái sang phải, trên xuống dưới. Shot đầu bắt đầu từ 0, các shot liên tiếp không hở không chồng, số giây nguyên, endSeconds của shot cuối bằng {config.TargetDurationPerSceneSeconds}. Không thêm giá, giảm giá, chứng nhận, công dụng hay biến thể sản phẩm không có trong dữ liệu đầu vào.");
        }
        else if (singleVideo)
        {
            sb.AppendLine($"Chỉ trả về JSON hợp lệ, không markdown. Schema: {{\"durationSeconds\":{config.TargetDurationPerSceneSeconds},\"shots\":[{{\"startSeconds\":0,\"endSeconds\":5,\"description\":\"mô tả hành động của đoạn\"}}]}}. Các shot liên tiếp, shot đầu bắt đầu từ 0, endSeconds của shot cuối bằng durationSeconds, tối đa 30 shot.");
        }
        else
        {
            sb.AppendLine($"Chỉ trả về JSON hợp lệ, không Markdown. Schema: {{\"scenes\":[{{\"name\":\"tên cảnh\",\"imagePrompt\":\"prompt ảnh tĩnh của cảnh\",\"videoPrompt\":\"English render instructions for {config.TargetDurationPerSceneSeconds} seconds\",\"tiktokContent\":\"Caption TikTok theo quy tắc ở trên\"}}]}}. Trả đúng {config.TargetSceneCount} cảnh.");
        }

        return new VideoPromptCompositionResult(
            TemplateId: templateId,
            TemplateName: templateName,
            ComposedPrompt: sb.ToString().TrimEnd(),
            OutputContractVersion: compactGrid ? StoryboardContentService.CompactGridSchemaVersion : 1
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

    /// <summary>Purpose của mẫu quy định cách viết caption TikTok.</summary>
    public const string TikTokContentPurpose = "TikTokContent";

    private static async Task AppendTikTokContentGuidanceAsync(
        StringBuilder sb, FrayloDbContext dbContext, CancellationToken cancellationToken)
    {
        var template = await FindActiveTemplateAsync(dbContext, TikTokContentPurpose, cancellationToken).ConfigureAwait(false);
        var text = template?.TemplateText ?? BuiltInPrompts[TikTokContentPurpose].TemplateText;
        sb.AppendLine("[QUY TẮC VIẾT NỘI DUNG ĐĂNG TIKTOK]");
        sb.AppendLine("Áp dụng cho trường tiktokContent/tiktok_content (ưu tiên hơn mô tả trường trong OUTPUT_CONTRACT):");
        sb.AppendLine(text.Trim());
        sb.AppendLine();
    }

    /// <summary>
    /// Chọn prompt riêng của dự án, mẫu toàn cục đang dùng hoặc mẫu mặc định theo thứ tự ưu tiên.
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

        var template = await FindActiveTemplateAsync(dbContext, purpose, cancellationToken).ConfigureAwait(false);
        if (template != null)
            return new ResolvedPrompt(template.Id, $"Mẫu {template.Purpose} toàn cục", template.TemplateText);

        var builtIn = BuiltInPrompts[purpose];
        return new ResolvedPrompt($"builtin-{purpose}", builtIn.Name, builtIn.TemplateText);
    }

}
