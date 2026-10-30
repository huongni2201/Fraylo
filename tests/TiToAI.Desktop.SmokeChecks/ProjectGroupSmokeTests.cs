using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TiToAI.Application.Content;
using TiToAI.Application.Projects;
using TiToAI.Contracts.Content;
using TiToAI.Contracts.Jobs;
using TiToAI.Contracts.Projects;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Desktop.SmokeChecks;

/// <summary>
/// Kiểm thử Nhóm dự án: CRUD, kiểm tra trùng tên, gán Dự án, xóa nhóm giữ lại Dự án,
/// và chọn prompt theo thứ tự ưu tiên Dự án > Nhóm > mẫu toàn cục.
/// </summary>
public static class ProjectGroupSmokeTests
{
    public static async Task RunAsync(DbContextOptions<TiToAIDbContext> options)
    {
        var groups = new ProjectGroupService(options);
        var projects = new ProjectService(options);
        var workflow = new ProjectWorkflowService(options);
        var composer = new VideoWorkflowPromptComposer(options);

        var group = await groups.SaveAsync(new ProjectGroupSaveRequest(
            GroupId: null,
            Name: "Quần áo",
            Description: "Video thời trang",
            ScriptGuidance: "GUIDE_NHOM_SCRIPT {giu_nguyen}",
            VideoGuidance: "GUIDE_NHOM_VIDEO"));
        Assert(group.ProjectCount == 0 && group.ScriptGuidance == "GUIDE_NHOM_SCRIPT {giu_nguyen}", "Nhóm mới phải lưu đủ prompt, dấu ngoặc nhọn giữ nguyên vì không còn biến.");
        Assert((await groups.ListAllAsync()).Count == 1, "projectgroup.list phải liệt kê nhóm vừa tạo.");

        await ExpectValidationFailureAsync(() => groups.SaveAsync(new ProjectGroupSaveRequest(null, " quần áo ", null, null, null)),
            "Tên nhóm trùng không phân biệt hoa thường phải bị từ chối.");

        var project = await projects.CreateAsync(new ProjectCreateRequest("Áo thun 1", null, group.GroupId));
        var detail = await projects.GetAsync(project.ProjectId);
        Assert(detail.GroupId == group.GroupId && detail.GroupName == "Quần áo", "Dự án tạo trong nhóm phải trả về tên nhóm.");
        Assert((await groups.ListAllAsync()).Single().ProjectCount == 1, "projectgroup.list phải đếm đúng số Dự án trong nhóm.");
        var listed = (await projects.ListAllAsync()).Single(p => p.ProjectId == project.ProjectId);
        Assert(listed.GroupId == group.GroupId && listed.GroupName == "Quần áo", "project.list phải trả về nhóm của từng Dự án.");

        await ExpectNotFoundAsync(() => projects.CreateAsync(new ProjectCreateRequest("Lạc nhóm", null, Guid.NewGuid().ToString("D"))),
            "Gán Dự án vào nhóm không tồn tại phải bị từ chối.");

        SaveProjectWorkflowConfigRequest ConfigRequest(string projectId, string? scriptGuidance = null, string? videoGuidance = null) => new(
            ProjectId: projectId,
            WorkflowMode: WorkflowModes.ProductOnly,
            GoalPrompt: "",
            ModelAssetId: null,
            ProductAssetId: null,
            ReadyImageAssetId: null,
            AudioOption: AudioOptions.None,
            AudioAssetId: null,
            TargetSceneCount: 1,
            TargetDurationPerSceneSeconds: 15,
            ScriptGuidance: scriptGuidance,
            VideoGuidance: videoGuidance);
        await workflow.SaveWorkflowConfigAsync(ConfigRequest(project.ProjectId));

        await using (var db = new TiToAIDbContext(options))
        {
            db.PromptTemplates.Add(new PromptTemplate
            {
                Id = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                Purpose = JobTaskType.Script,
                Name = "Mẫu kiểm thử nhóm",
                IsActive = true,
                TemplateText = "MAU_TOAN_CUC_SCRIPT {khong_render}",
                CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                UpdatedAtUtc = DateTime.UtcNow.ToString("o")
            });
            await db.SaveChangesAsync();
        }

        var config = await workflow.GetWorkflowConfigAsync(project.ProjectId);
        Assert(config.GroupId == group.GroupId && config.GroupName == "Quần áo", "Cấu hình Dự án phải kèm nhóm để giao diện hiển thị.");

        // Ưu tiên 2: Dự án không có prompt riêng thì dùng prompt Nhóm, thay hẳn mẫu toàn cục.
        var script = await composer.ComposeScriptPlanningAiRequestAsync(config);
        Assert(script.ComposedPrompt.StartsWith("GUIDE_NHOM_SCRIPT {giu_nguyen}", StringComparison.Ordinal), "Prompt Script phải bắt đầu bằng nội dung đã chọn, không thêm nhãn.");
        Assert(script.ComposedPrompt.Contains("GUIDE_NHOM_SCRIPT {giu_nguyen}"), "Prompt Script của Nhóm phải được dùng nguyên văn.");
        Assert(!script.ComposedPrompt.Contains("MAU_TOAN_CUC_SCRIPT"), "Có prompt Nhóm thì không được ghép mẫu toàn cục.");
        Assert(script.TemplateName.Contains("nhóm"), "Tên nguồn phải cho biết đang dùng prompt Nhóm.");
        Assert(script.ComposedPrompt.IndexOf("GUIDE_NHOM_SCRIPT", StringComparison.Ordinal) < script.ComposedPrompt.IndexOf("[OUTPUT_CONTRACT]", StringComparison.Ordinal),
            "Prompt phải nằm trước khuôn đầu ra OUTPUT_CONTRACT.");
        foreach (var strategy in new[] { VideoStrategies.MultiVideo, VideoStrategies.SingleVideo })
        {
            var composed = await composer.ComposeScriptPlanningAiRequestAsync(config with { VideoStrategy = strategy });
            Assert(!composed.ComposedPrompt.Contains("[NHIỆM VỤ]") && !composed.ComposedPrompt.Contains("[DỮ LIỆU LẦN TẠO")
                && !composed.ComposedPrompt.Contains("TOTAL_DURATION:") && !composed.ComposedPrompt.Contains("WORKFLOW_MODE:")
                && !composed.ComposedPrompt.Contains("VIDEO_STRATEGY:"), "Không tự nối nhiệm vụ hoặc dữ liệu lần tạo vào prompt Script.");
            Assert(composed.ComposedPrompt.Contains(strategy == VideoStrategies.SingleVideo ? "\"shots\"" : "\"scenes\""),
                "Prompt Script vẫn phải giữ schema đầu ra theo chiến lược video.");
        }

        // Ưu tiên 1: prompt riêng của Dự án thay cả prompt Nhóm.
        var projectConfig = await workflow.SaveWorkflowConfigAsync(ConfigRequest(project.ProjectId, scriptGuidance: "GUIDE_DU_AN_SCRIPT"));
        var projectScript = await composer.ComposeScriptPlanningAiRequestAsync(projectConfig);
        Assert(projectScript.ComposedPrompt.Contains("GUIDE_DU_AN_SCRIPT") && !projectScript.ComposedPrompt.Contains("GUIDE_NHOM_SCRIPT"),
            "Prompt của Dự án phải thay hẳn prompt Nhóm, không nối thêm.");
        Assert(projectScript.TemplateName.Contains("dự án"), "Tên nguồn phải cho biết đang dùng prompt Dự án.");

        var scene = new Scene
        {
            Id = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            ProjectId = project.ProjectId,
            SequenceOrder = 1,
            Name = "Cảnh 1",
            TargetDurationSeconds = 15,
            CreatedAtUtc = DateTime.UtcNow.ToString("o"),
            UpdatedAtUtc = DateTime.UtcNow.ToString("o")
        };
        var video = await composer.ComposeVideoPromptAiRequestAsync(config, scene);
        Assert(video.ComposedPrompt.Contains("GUIDE_NHOM_VIDEO"), "Prompt VideoPrompt của Nhóm phải được dùng khi Dự án để trống, kể cả khi chưa có mẫu VideoPrompt toàn cục.");

        // Tệp dùng chung trong Nhóm: tệp của dự án được chia sẻ lên Nhóm; dự án để trống ô sẽ dùng tệp mặc định của Nhóm.
        var otherGroupId = (await groups.SaveAsync(new ProjectGroupSaveRequest(null, "Giày", null, null, null))).GroupId;
        var otherProject = await projects.CreateAsync(new ProjectCreateRequest("Giày 1", null, otherGroupId));

        // Ưu tiên 3: Dự án và Nhóm đều trống thì dùng mẫu toàn cục nguyên văn (không điền biến).
        var fallbackScript = await composer.ComposeScriptPlanningAiRequestAsync(await workflow.GetWorkflowConfigAsync(otherProject.ProjectId));
        Assert(fallbackScript.ComposedPrompt.Contains("MAU_TOAN_CUC_SCRIPT {khong_render}"), "Không có prompt Dự án và Nhóm thì phải dùng mẫu toàn cục nguyên văn.");
        var ownProduct = NewAsset(project.ProjectId, null, "Image");
        var ownAudio = NewAsset(project.ProjectId, null, "Audio");
        var groupModel = NewAsset(null, group.GroupId, "Image");
        var groupAudio = NewAsset(null, group.GroupId, "Audio");
        var groupProduct2 = NewAsset(null, group.GroupId, "Image");
        var groupProduct3 = NewAsset(null, group.GroupId, "Image");
        var libraryImage = NewAsset(null, null, "Image");
        var otherImage = NewAsset(otherProject.ProjectId, null, "Image");
        await using (var db = new TiToAIDbContext(options))
        {
            db.Assets.AddRange(ownProduct, ownAudio, groupModel, groupAudio, groupProduct2, groupProduct3, libraryImage, otherImage);
            await db.SaveChangesAsync();
        }

        var saved = await groups.SaveAsync(new ProjectGroupSaveRequest(group.GroupId, "Quần áo", "Video thời trang",
            "GUIDE_NHOM_SCRIPT", "GUIDE_NHOM_VIDEO",
            ModelAssetId: groupModel.Id, ProductAssetId: ownProduct.Id, ReadyImageAssetId: null, AudioAssetId: groupAudio.Id));
        Assert(saved.ProductAssetId == ownProduct.Id && saved.ModelAssetId == groupModel.Id && saved.AudioAssetId == groupAudio.Id,
            "projectgroup.save phải trả về tệp mặc định của nhóm.");
        await using (var db = new TiToAIDbContext(options))
        {
            var promoted = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == ownProduct.Id);
            Assert(promoted.ProjectId is null && promoted.GroupId == group.GroupId,
                "Tệp của dự án trong nhóm khi đặt làm mặc định nhóm phải chuyển thành tệp của nhóm.");
        }

        await ExpectValidationFailureAsync(() => groups.SaveAsync(new ProjectGroupSaveRequest(group.GroupId, "Quần áo", null, null, null, ModelAssetId: otherImage.Id)),
            "Tệp thuộc dự án ngoài nhóm không được làm mặc định nhóm.");
        await ExpectValidationFailureAsync(() => groups.SaveAsync(new ProjectGroupSaveRequest(group.GroupId, "Quần áo", null, null, null, ReadyImageAssetId: libraryImage.Id)),
            "Tệp thư viện chung không được làm mặc định nhóm.");
        await ExpectValidationFailureAsync(() => groups.SaveAsync(new ProjectGroupSaveRequest(group.GroupId, "Quần áo", null, null, null, ProductAssetId: ownAudio.Id)),
            "Âm thanh không được đặt vào ô ảnh.");
        await ExpectValidationFailureAsync(() => groups.SaveAsync(new ProjectGroupSaveRequest(group.GroupId, "Quần áo", null, null, null, AudioAssetId: groupProduct2.Id)),
            "Ảnh không được đặt vào ô âm thanh.");

        // Dự án 2 và 3 trong nhóm: không có tệp riêng nên đọc ra tệp mặc định của nhóm.
        var p2 = await projects.CreateAsync(new ProjectCreateRequest("Áo thun 2", null, group.GroupId));
        var p3 = await projects.CreateAsync(new ProjectCreateRequest("Áo thun 3", null, group.GroupId));
        var p2Config = await workflow.GetWorkflowConfigAsync(p2.ProjectId);
        Assert(p2Config.VideoStrategy == VideoStrategies.SingleVideo, "Dự án mới phải mặc định tạo một video.");
        Assert(p2Config.ProductAssetId == ownProduct.Id && p2Config.ModelAssetId == groupModel.Id,
            "Dự án không có tệp riêng phải đọc ra tệp mặc định của nhóm.");
        Assert(p2Config.InheritedAssetSlots is { } inheritedSlots && inheritedSlots.Contains("Product") && inheritedSlots.Contains("Model"),
            "Cấu hình phải đánh dấu các ô đang kế thừa từ nhóm.");
        Assert(p2Config.AudioAssetId is null && !(p2Config.InheritedAssetSlots?.Contains("Audio") ?? false),
            "Âm thanh của nhóm chỉ áp dụng khi dự án dùng âm thanh tùy chọn.");

        // Lưu lại đúng tệp mặc định không được biến thành tệp riêng: dự án vẫn theo nhóm.
        await SaveWorkflowAssetsAsync(workflow, p2.ProjectId, groupModel.Id, ownProduct.Id, null, AudioOptions.None, null);
        using (var stored = JsonDocument.Parse(await ReadStoredConfigJsonAsync(options, p2.ProjectId)))
        {
            Assert(stored.RootElement.GetProperty("modelAssetId").ValueKind == JsonValueKind.Null
                && stored.RootElement.GetProperty("productAssetId").ValueKind == JsonValueKind.Null,
                "Tệp trùng mặc định của nhóm không được lưu thành tệp riêng của dự án.");
        }
        Assert((await workflow.GetWorkflowConfigAsync(p2.ProjectId)).ProductAssetId == ownProduct.Id,
            "Dự án vẫn phải đọc ra tệp mặc định sau khi lưu lại.");

        // Tệp riêng của dự án được giữ, không còn đánh dấu kế thừa.
        await SaveWorkflowAssetsAsync(workflow, p2.ProjectId, groupModel.Id, groupProduct2.Id, null, AudioOptions.None, null);
        var p2Own = await workflow.GetWorkflowConfigAsync(p2.ProjectId);
        Assert(p2Own.ProductAssetId == groupProduct2.Id && !(p2Own.InheritedAssetSlots?.Contains("Product") ?? false),
            "Tệp riêng của dự án phải được giữ và không còn đánh dấu kế thừa.");

        // Đổi tệp mặc định của nhóm: dự án đang kế thừa đổi theo, dự án có tệp riêng giữ nguyên.
        await groups.SaveAsync(new ProjectGroupSaveRequest(group.GroupId, "Quần áo", "Video thời trang",
            "GUIDE_NHOM_SCRIPT", "GUIDE_NHOM_VIDEO",
            ModelAssetId: groupModel.Id, ProductAssetId: groupProduct3.Id, ReadyImageAssetId: null, AudioAssetId: groupAudio.Id));
        Assert((await workflow.GetWorkflowConfigAsync(p3.ProjectId)).ProductAssetId == groupProduct3.Id,
            "Dự án đang kế thừa phải theo tệp mặc định mới của nhóm.");
        Assert((await workflow.GetWorkflowConfigAsync(p2.ProjectId)).ProductAssetId == groupProduct2.Id,
            "Dự án có tệp riêng không được đổi theo nhóm.");

        // Âm thanh tùy chọn: dự án không có âm thanh riêng dùng âm thanh mặc định của nhóm.
        await SaveWorkflowAssetsAsync(workflow, p3.ProjectId, null, null, null, AudioOptions.CustomAudio, null);
        var p3Config = await workflow.GetWorkflowConfigAsync(p3.ProjectId);
        Assert(p3Config.AudioAssetId == groupAudio.Id && (p3Config.InheritedAssetSlots?.Contains("Audio") ?? false),
            "Âm thanh tùy chọn phải dùng âm thanh mặc định của nhóm.");

        // Dự án ngoài nhóm không được dùng tệp của nhóm.
        var freeProject = await projects.CreateAsync(new ProjectCreateRequest("Tự do", null, null));
        bool rejectedForeignGroupAsset = false;
        try
        {
            await SaveWorkflowAssetsAsync(workflow, freeProject.ProjectId, null, groupProduct3.Id, null, AudioOptions.None, null);
        }
        catch (UnauthorizedAccessException)
        {
            rejectedForeignGroupAsset = true;
        }
        Assert(rejectedForeignGroupAsset, "Dự án ngoài nhóm không được dùng tệp riêng của nhóm.");

        await groups.DeleteAsync(group.GroupId);
        var afterDelete = await projects.GetAsync(project.ProjectId);
        Assert(afterDelete.GroupId is null && afterDelete.GroupName is null,
            "Xóa nhóm phải giữ Dự án và chuyển về không nhóm.");
        Assert((await workflow.GetWorkflowConfigAsync(project.ProjectId)).GroupId is null, "Cấu hình Dự án sau khi xóa nhóm không được còn mã nhóm.");
        await using (var db = new TiToAIDbContext(options))
        {
            var detached = await db.Assets.AsNoTracking()
                .Where(a => a.Id == ownProduct.Id || a.Id == groupModel.Id || a.Id == groupAudio.Id)
                .ToListAsync();
            Assert(detached.Count == 3 && detached.All(a => a.GroupId is null && a.ProjectId is null),
                "Xóa nhóm chỉ bỏ liên kết tệp của nhóm (thành thư viện chung), không xóa tệp.");
        }
        Assert((await workflow.GetWorkflowConfigAsync(p3.ProjectId)).ProductAssetId is null,
            "Dự án sau khi rời nhóm không còn kế thừa tệp mặc định.");

        Console.WriteLine("[PASS] Project groups: CRUD, duplicate/guidance/variable validation, layered prompt composition, delete keeps projects.");
    }

    private static Asset NewAsset(string? projectId, string? groupId, string assetType) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ProjectId = projectId,
        GroupId = groupId,
        StorageRootId = "root-test",
        RelativePath = $"test/{Guid.NewGuid():N}.bin",
        AssetType = assetType,
        ByteSize = 1,
        Sha256Hash = new string('0', 64),
        Status = "Linked",
        CreatedAtUtc = DateTime.UtcNow.ToString("o")
    };

    private static async Task SaveWorkflowAssetsAsync(
        ProjectWorkflowService workflow,
        string projectId,
        string? modelAssetId,
        string? productAssetId,
        string? readyImageAssetId,
        string audioOption,
        string? audioAssetId)
    {
        await workflow.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
            ProjectId: projectId,
            WorkflowMode: WorkflowModes.ModelAndProduct,
            GoalPrompt: "",
            ModelAssetId: modelAssetId,
            ProductAssetId: productAssetId,
            ReadyImageAssetId: readyImageAssetId,
            AudioOption: audioOption,
            AudioAssetId: audioAssetId,
            TargetSceneCount: 1,
            TargetDurationPerSceneSeconds: 15));
    }

    private static async Task<string> ReadStoredConfigJsonAsync(DbContextOptions<TiToAIDbContext> options, string projectId)
    {
        await using var db = new TiToAIDbContext(options);
        var key = $"project.workflow.config.{projectId}";
        return await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleAsync();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ExpectValidationFailureAsync(Func<Task> action, string message)
    {
        try
        {
            await action();
        }
        catch (ProjectValidationException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static async Task ExpectNotFoundAsync(Func<Task> action, string message)
    {
        try
        {
            await action();
        }
        catch (ProjectGroupNotFoundException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
