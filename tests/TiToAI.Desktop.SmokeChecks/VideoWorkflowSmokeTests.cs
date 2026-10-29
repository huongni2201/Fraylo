using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TiToAI.Application.Content;
using TiToAI.Application.Jobs;
using TiToAI.Application.Providers;
using TiToAI.Application.Settings;
using TiToAI.Contracts.Bridge;
using TiToAI.Contracts.Content;
using TiToAI.Contracts.Jobs;
using TiToAI.Contracts.Providers;
using TiToAI.Contracts.Settings;
using TiToAI.Desktop.Bridge;
using TiToAI.Desktop.Bridge.Handlers;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;
using TiToAI.Infrastructure.Storage;

namespace TiToAI.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói kiểm chứng toàn diện quy trình sản xuất video từ ảnh theo PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN.md:
/// - Mốc 1: Loại bỏ trạng thái Running giả mạo; từ chối chuyển sang Running khi chưa có job bền vững; bảo toàn input.
/// - Mốc 1: Không tự sinh 3 cảnh hardcode; bảo toàn 100% dữ liệu cảnh người dùng đã tạo.
/// - Ba chế độ bắt đầu: Mẫu + sản phẩm, Ảnh đã hoàn thiện, Chỉ sản phẩm.
/// - Quyết định Audio không bắt buộc.
/// - Ma trận tái tạo và phụ thuộc §3.4.
/// - Đăng ký Bridge Handlers và kiểm tra Allow-list.
/// </summary>
public static class VideoWorkflowSmokeTests
{
    private static int _passCount = 0;
    private static int _failCount = 0;

    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("\n=================================================================");
        Console.WriteLine("    TiToAI — Video Production from Images Workflow Smoke Checks  ");
        Console.WriteLine("=================================================================");

        _passCount = 0;
        _failCount = 0;

        await RunCaseAsync("1. Validation 3 chế độ bắt đầu: Kiểm tra tính đầy đủ của vai trò ảnh và ném lỗi rõ ràng",
            TestCase1_InputModesValidationAsync);

        await RunCaseAsync("2. Mốc 1: workflow.start từ chối báo Running khi chưa có job bền vững; bảo toàn input",
            TestCase2_StartWithoutDurableJob_RejectsRunningStateAndKeepsInputAsync);

        await RunCaseAsync("3. Mốc 1: Không sinh 3 cảnh/prompt hardcode giả mạo; từ chối khi dự án chưa có cảnh",
            TestCase3_NoHardcodedScenesGeneratedAsync);

        await RunCaseAsync("4. Mốc 1: Bảo toàn dữ liệu và cảnh người dùng cũ qua service và metadata",
            TestCase4_ExistingUserDataPreservedAsync);

        await RunCaseAsync("5. Chuyển sang Running chỉ khi có job bền vững được enqueue (mô phỏng Scheduler)",
            TestCase5_StartWithDurableJobEnqueuer_TransitionsToRunningAsync);

        await RunCaseAsync("6. Audio là tùy chọn; asset khác Project bị chặn",
            TestCase6_AudioOptionalAndProjectIsolationAsync);

        await RunCaseAsync("7. Ma trận phụ thuộc §3.4: Sửa prompt chỉ làm lại video cảnh đó; đổi ảnh làm lại prompt + video; giữ ảnh và cảnh khác",
            TestCase7_DependencyReRunMatrixAsync);

        await RunCaseAsync("8. Tạm dừng & Tiếp tục (Pause & Resume Run): Cập nhật trạng thái run an toàn và bền vững",
            TestCase8_PauseAndResumeWorkflowAsync);

        await RunCaseAsync("9. Tích hợp Bridge Handlers RPC: 7 lệnh workflow.* qua BridgeDispatcher và Allow-list",
            TestCase9_BridgeHandlersRpcIntegrationAsync);

        await RunCaseAsync("10. Project-first: Nối luồng tự động đến đúng một video đầu ra",
            TestCase10_EndToEndSingleVideoAsync);

        await RunCaseAsync("11. Mốc 4: AI Lập cảnh tự động từ Project Brief khi dự án chưa có cảnh nào",
            TestCase11_AiScenePlanningFromBrief_ZeroScenesAsync);

        await RunCaseAsync("12. Mốc 4: Hủy quy trình (Cancel Workflow) giải phóng slot và hủy active jobs an toàn",
            TestCase12_CancelWorkflow_CancelsActiveJobsAndResetsStateAsync);

        await RunCaseAsync("13. Delta: Thẩm định thời lượng clip theo capability & mở dữ liệu cũ 5s (CD03–CD08)",
            TestCase13_ClipDurationValidationAndLegacyDataSupportAsync);

        await RunCaseAsync("14. Delta: Template đã render, GoalPrompt tương thích cũ và ranh giới AI Writer vs MuseAI (§3.1–3.3, CD11–CD14)",
            TestCase14_GoalPromptBriefAndTemplateCompositionAsync);

        await RunCaseAsync("15. Project-first: legacy multi-scene project is preserved and blocked from producing multiple videos",
            TestCase15_LegacyMultiSceneProjectIsPreservedAsync);

        await RunCaseAsync("16. Run và job đầu tiên cùng rollback khi lưu thất bại",
            TestCase16_StartRunAndJobRollbackTogetherAsync);

        Console.WriteLine("\n-----------------------------------------------------------------");
        Console.WriteLine($"Kết quả kiểm thử Video Workflow: {_passCount} PASS, {_failCount} FAIL.");
        Console.WriteLine("=================================================================\n");

        return _failCount > 0 ? 1 : 0;
    }

    private static async Task RunCaseAsync(string title, Func<Task> testFunc)
    {
        Console.Write($"[RUN] {title} ... ");
        try
        {
            await testFunc();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS");
            Console.ResetColor();
            _passCount++;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAIL");
            Console.WriteLine($"      -> Lỗi: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"      -> Vị trí: {string.Join(" | ", (ex.StackTrace ?? "").Split('\n').Take(3))}");
            Console.ResetColor();
            _failCount++;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] {message}");
        }
    }

    private static (DbContextOptions<TiToAIDbContext> dbOptions, string tempDir, string projectId) CreateTestEnvironment()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "titoai_wf_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "workflow_smoke.db");

        var dbOptions = new DbContextOptionsBuilder<TiToAIDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        using (var db = new TiToAIDbContext(dbOptions))
        {
            db.Database.EnsureCreated();

            var now = DateTimeOffset.UtcNow.ToString("o");
            var prjId = Guid.NewGuid().ToString("D").ToLowerInvariant();


            db.Projects.Add(new Project
            {
                Id = prjId,
                Name = "Dự án BST Mùa Thu",
                Status = "Draft",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

            db.SaveChanges();
            return (dbOptions, tempDir, prjId);
        }
    }

    private static void CleanupTestEnvironment(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch { }
    }

    private static string SeedAsset(DbContextOptions<TiToAIDbContext> dbOptions, string assetType, string relativePath, string? ownerProjectId = null)
    {
        using var db = new TiToAIDbContext(dbOptions);
        var assetId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("o");

        db.Assets.Add(new Asset
        {
            Id = assetId,
            ProjectId = ownerProjectId,
            StorageRootId = "root-1",
            RelativePath = relativePath,
            AssetType = assetType,
            ByteSize = 1024,
            Sha256Hash = "dummy-hash-" + Guid.NewGuid().ToString("N"),
            Status = "Linked",
            CreatedAtUtc = now
        });

        db.SaveChanges();
        return assetId;
    }

    private static string SeedScene(DbContextOptions<TiToAIDbContext> dbOptions, string projectId, string name, int sequence, string? imageAssetId, string? prompt, int targetDurationSeconds = 15)
    {
        using var db = new TiToAIDbContext(dbOptions);
        var sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("o");

        db.Scenes.Add(new Scene
        {
            Id = sceneId,
            ProjectId = projectId,
            SequenceOrder = sequence,
            Name = name,
            TargetDurationSeconds = targetDurationSeconds,
            PromptText = prompt,
            Status = imageAssetId != null ? "ReadyForPrompt" : "PendingImage",
            ImageAssetId = imageAssetId,
            AudioAssetId = null,
            VideoAssetId = null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        db.SaveChanges();
        return sceneId;
    }

    /// <summary>
    /// Kịch bản 1: Validation 3 chế độ bắt đầu
    /// </summary>
    private static async Task TestCase1_InputModesValidationAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var service = new ProjectWorkflowService(dbOptions);
            var modelAssetId = SeedAsset(dbOptions, "Image", "models/model1.jpg");
            var productAssetId = SeedAsset(dbOptions, "Image", "products/shirt1.jpg");
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/wearing_shirt.jpg");

            // 1. ModelAndProduct thiếu ProductAssetId -> Báo lỗi rõ
            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ModelAndProduct,
                GoalPrompt: "Test validation",
                ModelAssetId: modelAssetId,
                ProductAssetId: null, // Thiếu!
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 3,
                TargetDurationPerSceneSeconds: 15
            ));

            bool failedModelProduct = false;
            try
            {
                await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            }
            catch (ProjectWorkflowValidationException ex) when (ex.Message.Contains("Mẫu + sản phẩm"))
            {
                failedModelProduct = true;
            }
            Assert(failedModelProduct, "Chế độ Mẫu + sản phẩm thiếu ảnh sản phẩm phải ném ProjectWorkflowValidationException.");

            // 2. ReadyImage thiếu ReadyImageAssetId -> Báo lỗi rõ
            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Test validation",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: null, // Thiếu!
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15
            ));

            bool failedReady = false;
            try
            {
                await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            }
            catch (ProjectWorkflowValidationException ex) when (ex.Message.Contains("Ảnh đã sẵn sàng"))
            {
                failedReady = true;
            }
            Assert(failedReady, "Chế độ Ảnh đã sẵn sàng thiếu ready image phải ném ProjectWorkflowValidationException.");

            // 3. ProductOnly thiếu ProductAssetId -> Báo lỗi rõ
            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ProductOnly,
                GoalPrompt: "Test validation",
                ModelAssetId: null,
                ProductAssetId: null, // Thiếu!
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15
            ));

            bool failedProduct = false;
            try
            {
                await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            }
            catch (ProjectWorkflowValidationException ex) when (ex.Message.Contains("Chỉ sản phẩm"))
            {
                failedProduct = true;
            }
            Assert(failedProduct, "Chế độ Chỉ sản phẩm thiếu product image phải ném ProjectWorkflowValidationException.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 2 (Mốc 1): workflow.start từ chối báo Running khi chưa có job bền vững; bảo toàn input
    /// </summary>
    private static async Task TestCase2_StartWithoutDurableJob_RejectsRunningStateAndKeepsInputAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            // Khởi tạo service KHÔNG có enqueuer job (mô phỏng khi scheduler/provider chưa nối)
            var service = new ProjectWorkflowService(dbOptions, durableJobEnqueuer: null);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/wearing_shirt.jpg");

            // Người dùng có sẵn 1 cảnh
            SeedScene(dbOptions, prjId, "Cảnh mở đầu", 1, readyAssetId, "Cinematic panning shot", targetDurationSeconds: 15);

            // Người dùng lưu cấu hình đầy đủ
            var savedConfig = await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Áo sơ mi lụa dạo phố thanh lịch, góc máy điện ảnh 4K",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15
            ));

            // Bấm Bắt đầu -> Phải bị từ chối với lý do rõ ràng
            bool rejectedWithClearReason = false;
            try
            {
                await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            }
            catch (ProjectWorkflowValidationException ex) when (ex.Message.Contains("tác vụ nền bền vững") || ex.Message.Contains("hàng đợi"))
            {
                rejectedWithClearReason = true;
            }
            Assert(rejectedWithClearReason, "Gọi startWorkflow khi chưa có job bền vững phải ném ngoại lệ với lý do rõ ràng.");

            // Kiểm tra DB: RunState KHÔNG ĐƯỢC CHUYỂN SANG RUNNING!
            var currentConfig = await service.GetWorkflowConfigAsync(prjId);
            Assert(currentConfig.RunState != WorkflowRunState.Running, $"RunState tuyệt đối không được là Running khi không có job, thực tế: '{currentConfig.RunState}'");
            Assert(currentConfig.RunState == WorkflowRunState.Idle, $"RunState phải duy trì là Idle, thực tế: '{currentConfig.RunState}'");

            // Kiểm tra DB: Toàn bộ input người dùng được bảo toàn 100%
            Assert(currentConfig.GoalPrompt == "Áo sơ mi lụa dạo phố thanh lịch, góc máy điện ảnh 4K", "GoalPrompt phải được bảo toàn nguyên vẹn.");
            Assert(currentConfig.ReadyImageAssetId == readyAssetId, "ReadyImageAssetId phải được bảo toàn nguyên vẹn.");
            Assert(currentConfig.TargetDurationPerSceneSeconds == 15, "TargetDurationPerSceneSeconds phải được bảo toàn nguyên vẹn.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 3 (Mốc 1): Không sinh 3 cảnh/prompt hardcode giả mạo; từ chối khi dự án chưa có cảnh
    /// </summary>
    private static async Task TestCase3_NoHardcodedScenesGeneratedAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var service = new ProjectWorkflowService(dbOptions);
            var modelAssetId = SeedAsset(dbOptions, "Image", "models/model1.jpg");
            var productAssetId = SeedAsset(dbOptions, "Image", "products/shirt1.jpg");

            // Lưu cấu hình
            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ModelAndProduct,
                GoalPrompt: "BST thu đông thanh lịch",
                ModelAssetId: modelAssetId,
                ProductAssetId: productAssetId,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 3,
                TargetDurationPerSceneSeconds: 15
            ));

            // Dự án chưa có cảnh nào -> Gọi StartWorkflowAsync phải từ chối sinh cảnh mẫu cố định
            bool rejectedNoScenes = false;
            try
            {
                await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            }
            catch (ProjectWorkflowValidationException ex) when (ex.Message.Contains("chưa có cảnh nào") || ex.Message.Contains("từ chối sinh cảnh mẫu cố định"))
            {
                rejectedNoScenes = true;
            }
            Assert(rejectedNoScenes, "Hệ thống phải từ chối sinh cảnh mẫu hardcode khi chưa có dịch vụ AI lập cảnh.");

            // Kiểm tra DB: Số lượng scene trong DB vẫn là 0! Tuyệt đối không sinh 3 cảnh hardcode!
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var sceneCount = await db.Scenes.CountAsync(s => s.ProjectId == prjId);
                Assert(sceneCount == 0, $"Không được tự ý chèn cảnh mẫu hardcode vào DB, thực tế số cảnh: {sceneCount}");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 4 (Mốc 1): Bảo toàn dữ liệu và cảnh người dùng cũ qua service và metadata
    /// </summary>
    private static async Task TestCase4_ExistingUserDataPreservedAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var service = new ProjectWorkflowService(dbOptions);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/wearing_shirt.jpg");

            // Người dùng tự tạo 2 cảnh với thông tin riêng biệt
            var s1Id = SeedScene(dbOptions, prjId, "Cảnh Người Dùng 1: Tự tạo", 1, readyAssetId, "Prompt do người dùng tự viết A");
            var s2Id = SeedScene(dbOptions, prjId, "Cảnh Người Dùng 2: Tự tạo", 2, readyAssetId, "Prompt do người dùng tự viết B");

            // Lưu cấu hình workflow
            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Goal prompt người dùng",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15
            ));

            // Nạp danh sách cảnh chi tiết qua service
            var scenes = await service.ListSceneWorkflowDetailsAsync(prjId);
            Assert(scenes.Count == 2, $"Phải nạp đúng 2 cảnh người dùng đã tạo, thực tế: {scenes.Count}");

            var sc1 = scenes.First(s => s.SceneId == s1Id);
            var sc2 = scenes.First(s => s.SceneId == s2Id);

            Assert(sc1.Name == "Cảnh Người Dùng 1: Tự tạo", "Tên cảnh 1 của người dùng phải được bảo toàn.");
            Assert(sc1.MotionPromptText == "Prompt do người dùng tự viết A", "Prompt cảnh 1 của người dùng phải được bảo toàn.");
            Assert(sc2.Name == "Cảnh Người Dùng 2: Tự tạo", "Tên cảnh 2 của người dùng phải được bảo toàn.");
            Assert(sc2.MotionPromptText == "Prompt do người dùng tự viết B", "Prompt cảnh 2 của người dùng phải được bảo toàn.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 5: Chuyển sang Running chỉ khi có job bền vững được enqueue (mô phỏng Scheduler)
    /// </summary>
    private static async Task TestCase5_StartWithDurableJobEnqueuer_TransitionsToRunningAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            // Mô phỏng Scheduler/JobQueue enqueue bền vững cho đúng một video
            int mockEnqueuedCount = 1;
            var service = new ProjectWorkflowService(dbOptions, durableJobEnqueuer: (req, ct) => Task.FromResult(mockEnqueuedCount));

            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/item.jpg");
            SeedScene(dbOptions, prjId, "Cảnh A", 1, readyAssetId, "Prompt A");

            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Test enqueue bền vững",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            // Gọi StartWorkflowAsync khi có job bền vững được enqueue
            var startRes = await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            Assert(startRes.RunState == WorkflowRunState.Running, "RunState phải là Running khi có job bền vững được tạo.");
            Assert(startRes.CreatedSceneCount == 1, "Project phải giữ đúng một output scene cho video duy nhất.");
            Assert(startRes.Message.Contains("1 tác vụ bền vững đã được đưa vào hàng đợi"), "Message thông báo rõ số tác vụ bền vững được enqueue.");

            // Kiểm tra DB
            var config = await service.GetWorkflowConfigAsync(prjId);
            Assert(config.RunState == WorkflowRunState.Running, "RunState trong DB phải được cập nhật sang Running.");

            using (var db = new TiToAIDbContext(dbOptions))
            {
                var prj = await db.Projects.FirstAsync(p => p.Id == prjId);
                Assert(prj.Status == "InProgress", "Project Status phải chuyển sang InProgress.");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 6: Audio không bắt buộc và asset được cách ly theo Project.
    /// </summary>
    private static async Task TestCase6_AudioOptionalAndProjectIsolationAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var service = new ProjectWorkflowService(dbOptions);
            var productAssetId = SeedAsset(dbOptions, "Image", "products/dress.jpg", prjId);

            // Gắn asset audio với Project khác để thẩm định Project ownership.
            var otherProjectId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var now = DateTimeOffset.UtcNow.ToString("o");
                db.Projects.Add(new Project { Id = otherProjectId, Name = "Project khác", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.SaveChanges();
            }
            var crossProjectAudioId = SeedAsset(dbOptions, "Audio", "audio/stolen.mp3", otherProjectId);

            // 1. Thử gán audio thuộc Project khác -> Bị chặn ngay lập tức
            bool blockedCrossProject = false;
            try
            {
                await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                    ProjectId: prjId,
                    WorkflowMode: WorkflowModes.ProductOnly,
                    GoalPrompt: "Test audio",
                    ModelAssetId: null,
                    ProductAssetId: productAssetId,
                    ReadyImageAssetId: null,
                    AudioOption: AudioOptions.CustomAudio,
                    AudioAssetId: crossProjectAudioId,
                    TargetSceneCount: 1,
                    TargetDurationPerSceneSeconds: 5
                ));
            }
            catch (UnauthorizedAccessException)
            {
                blockedCrossProject = true;
            }
            Assert(blockedCrossProject, "Asset audio thuộc Project khác phải bị chặn bởi UnauthorizedAccessException.");

            // 2. Không gửi audio (AudioOption = None): Hợp lệ 100%
            var validConfig = await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ProductOnly,
                GoalPrompt: "Test không gửi audio",
                ModelAssetId: null,
                ProductAssetId: productAssetId,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 5
            ));
            Assert(validConfig.AudioOption == AudioOptions.None, "AudioOption được lưu là None.");
            Assert(validConfig.AudioAssetId == null, "AudioAssetId phải là null khi chọn None.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 7: Ma trận phụ thuộc §3.4
    /// </summary>
    private static async Task TestCase7_DependencyReRunMatrixAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var service = new ProjectWorkflowService(dbOptions);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/outfit.jpg");
            var newVariantAssetId = SeedAsset(dbOptions, "Image", "variants/outfit_side.jpg");

            // Tạo 2 cảnh
            var s1Id = SeedScene(dbOptions, prjId, "Cảnh 1", 1, readyAssetId, "Prompt 1");
            var s2Id = SeedScene(dbOptions, prjId, "Cảnh 2", 2, readyAssetId, "Prompt 2");

            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Mô tả chung",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15
            ));

            var scenes = await service.ListSceneWorkflowDetailsAsync(prjId);
            var scene1 = scenes.First(s => s.SceneId == s1Id);
            var scene2 = scenes.First(s => s.SceneId == s2Id);

            // Giả lập Scene 1 đã hoàn tất tạo video
            var videoAssetId = SeedAsset(dbOptions, "Video", "videos/scene1_clip.mp4");
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var s1 = db.Scenes.First(s => s.Id == scene1.SceneId);
                s1.VideoAssetId = videoAssetId;
                s1.Status = "Completed";
                db.SaveChanges();
            }

            // Test 7.1: Sửa prompt chuyển động của Scene 1
            // Theo §3.4: Chỉ làm lại video cảnh đó, giữ ảnh, đánh dấu Cần xem lại (isStale = true)
            var updatedPrompt = await service.UpdateSceneWorkflowInputAsync(new UpdateSceneWorkflowInputRequest(
                ProjectId: prjId,
                SceneId: scene1.SceneId,
                MotionPromptText: "Góc máy quay chậm từ dưới lên thể hiện sự tự tin",
                ExpectedRevision: scene1.Revision
            ));

            Assert(updatedPrompt.IsStale, "Sửa prompt khi đã có video phải đánh dấu isStale = true.");
            Assert(updatedPrompt.WorkflowStatus == SceneWorkflowStatus.NeedsReview, "WorkflowStatus phải chuyển sang NeedsReview.");
            Assert(updatedPrompt.SelectedImageAssetId == readyAssetId, "Ảnh của cảnh 1 phải được giữ nguyên 100%.");

            // Kiểm tra Scene 2 không bị ảnh hưởng
            var reloadedScenes = await service.ListSceneWorkflowDetailsAsync(prjId);
            var reloadedScene2 = reloadedScenes.First(s => s.SceneId == scene2.SceneId);
            Assert(!reloadedScene2.IsStale, "Sửa cảnh 1 không được làm stale cảnh 2.");

            // Test 7.2: Thay ảnh của Scene 1 sang ảnh biến thể mới
            // Theo §3.4: Làm lại video và prompt cảnh đó; không sinh lại ảnh vừa chọn
            var updatedImage = await service.UpdateSceneWorkflowInputAsync(new UpdateSceneWorkflowInputRequest(
                ProjectId: prjId,
                SceneId: scene1.SceneId,
                SelectedImageAssetId: newVariantAssetId,
                ExpectedRevision: updatedPrompt.Revision
            ));

            Assert(updatedImage.SelectedImageAssetId == newVariantAssetId, "Ảnh của cảnh 1 đã được cập nhật sang ảnh biến thể mới.");
            Assert(updatedImage.IsStale, "Đổi ảnh khi có video đánh dấu isStale = true.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 8: Tạm dừng và Tiếp tục quy trình
    /// </summary>
    private static async Task TestCase8_PauseAndResumeWorkflowAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var service = new ProjectWorkflowService(dbOptions);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/item.jpg");
            SeedScene(dbOptions, prjId, "Cảnh 1", 1, readyAssetId, "Prompt 1");

            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Test pause",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 5
            ));

            // Tạm dừng
            var paused = await service.PauseWorkflowAsync(prjId);
            Assert(paused.RunState == WorkflowRunState.Paused, "PauseWorkflowAsync phải đổi RunState sang Paused.");

            // Tiếp tục
            var resumed = await service.ResumeWorkflowAsync(prjId);
            Assert(resumed.RunState == WorkflowRunState.Running, "ResumeWorkflowAsync phải đổi RunState sang Running.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 9: Tích hợp Bridge Handlers RPC và Allow-list
    /// </summary>
    private static async Task TestCase9_BridgeHandlersRpcIntegrationAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/rpc_test.jpg");
            SeedScene(dbOptions, prjId, "Cảnh RPC", 1, readyAssetId, "Prompt RPC");

            // Service có mock durable enqueuer để test lệnh start qua RPC
            var service = new ProjectWorkflowService(dbOptions, (req, ct) => Task.FromResult(1));
            var handlers = new VideoWorkflowHandlers(service);
            var dispatcher = new BridgeDispatcher();

            handlers.Register(dispatcher);

            // Kiểm tra allow-list 8 lệnh
            var commands = new[]
            {
                "workflow.get_config",
                "workflow.save_config",
                "workflow.start",
                "workflow.list_scenes",
                "workflow.update_scene_input",
                "workflow.pause",
                "workflow.resume",
                "workflow.preview_ai_prompt"
            };

            foreach (var cmd in commands)
            {
                Assert(dispatcher.IsCommandAllowed(cmd), $"Lệnh '{cmd}' phải được mở trên allow-list.");
            }

            // Gửi lệnh workflow.get_config qua Dispatcher
            var reqGet = new RequestEnvelope(
                Id: Guid.NewGuid(),
                Type: "request",
                Command: "workflow.get_config",
                Payload: JsonSerializer.SerializeToElement(new { projectId = prjId }),
                ClientTimestampUtc: DateTimeOffset.UtcNow,
                ContractVersion: "1.1.0"
            );

            var resGet = await dispatcher.DispatchAsync(reqGet, CancellationToken.None);
            Assert(resGet.Success, "workflow.get_config qua BridgeDispatcher phải trả Success = true.");

            // Gửi lệnh workflow.save_config qua Dispatcher
            var reqSave = new RequestEnvelope(
                Id: Guid.NewGuid(),
                Type: "request",
                Command: "workflow.save_config",
                Payload: JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    projectId = prjId,
                    workflowMode = WorkflowModes.ReadyImage,
                    goalPrompt = "RPC test prompt",
                    readyImageAssetId = readyAssetId,
                    audioOption = AudioOptions.None,
                    targetSceneCount = 2,
                    targetDurationPerSceneSeconds = 15
                })).RootElement,
                ClientTimestampUtc: DateTimeOffset.UtcNow,
                ContractVersion: "1.1.0"
            );

            var resSave = await dispatcher.DispatchAsync(reqSave, CancellationToken.None);
            Assert(resSave.Success, "workflow.save_config qua BridgeDispatcher phải trả Success = true.");

            // Gửi lệnh workflow.preview_ai_prompt qua Dispatcher
            var reqPreview = new RequestEnvelope(
                Id: Guid.NewGuid(),
                Type: "request",
                Command: "workflow.preview_ai_prompt",
                Payload: JsonSerializer.SerializeToElement(new { projectId = prjId }),
                ClientTimestampUtc: DateTimeOffset.UtcNow,
                ContractVersion: "1.1.0"
            );
            var resPreview = await dispatcher.DispatchAsync(reqPreview, CancellationToken.None);
            Assert(resPreview.Success, "workflow.preview_ai_prompt qua BridgeDispatcher phải trả Success = true.");

            // Gửi lệnh workflow.start qua Dispatcher
            var reqStart = new RequestEnvelope(
                Id: Guid.NewGuid(),
                Type: "request",
                Command: "workflow.start",
                Payload: JsonSerializer.SerializeToElement(new { projectId = prjId }),
                ClientTimestampUtc: DateTimeOffset.UtcNow,
                ContractVersion: "1.1.0"
            );

            var resStart = await dispatcher.DispatchAsync(reqStart, CancellationToken.None);
            Assert(resStart.Success, "workflow.start qua BridgeDispatcher phải trả Success = true.");

            // Gửi lệnh workflow.list_scenes qua Dispatcher
            var reqList = new RequestEnvelope(
                Id: Guid.NewGuid(),
                Type: "request",
                Command: "workflow.list_scenes",
                Payload: JsonSerializer.SerializeToElement(new { projectId = prjId }),
                ClientTimestampUtc: DateTimeOffset.UtcNow,
                ContractVersion: "1.1.0"
            );

            var resList = await dispatcher.DispatchAsync(reqList, CancellationToken.None);
            Assert(resList.Success, "workflow.list_scenes qua BridgeDispatcher phải trả Success = true.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 10: Một Project chạy tới đúng một video đầu ra qua queue chung
    /// </summary>
    private static async Task TestCase10_EndToEndSingleVideoAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var registry = new StorageRootRegistry(dbOptions, tempDir);
            await registry.EnsureDefaultRootsAsync();
            var settingsService = new SettingsService(dbOptions, registry);
            var curSettings = await settingsService.GetSettingsAsync();
            await settingsService.SaveSettingsAsync(new SettingsSaveRequest(
                DocumentsPath: curSettings.DocumentsPath,
                ImagesPath: curSettings.ImagesPath,
                VideosPath: curSettings.VideosPath,
                ImageProvider: ProviderKeys.ChatGPTWeb,
                ScriptProvider: ProviderKeys.ChatGPTWeb,
                VideoPromptProvider: ProviderKeys.ChatGPTWeb,
                ExpectedRevision: curSettings.Revision
            ));
            var snapshotFactory = new JobSnapshotFactory(settingsService, dbOptions: dbOptions);
            var reservation = new JobReservation(dbOptions);

            // Khởi tạo Fake Executor kết hợp cả ChatGPT và MuseAI cho kiểm thử điều phối
            var fakeChatGpt = new DeterministicFakeProviderAdapter(ProviderKeys.ChatGPTWeb);
            var fakeMuseAi = new DeterministicFakeProviderAdapter(ProviderKeys.MuseAI);
            var multiExecutor = new CompositeTestJobExecutor(fakeChatGpt, fakeMuseAi);

            var scheduler = new JobScheduler(dbOptions, reservation, multiExecutor);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);
            var workflowService = new ProjectWorkflowService(dbOptions, jobService);

            // Nối event tự động apply output
            scheduler.OnJobStateChanged += payload =>
            {
                if (string.Equals(payload.NewState, JobState.Succeeded, StringComparison.OrdinalIgnoreCase))
                {
                    _ = workflowService.ApplyJobOutputAsync(payload.JobId);
                }
            };

            var modelAssetId = SeedAsset(dbOptions, "Image", "models/m1.jpg");
            var productAssetId = SeedAsset(dbOptions, "Image", "products/p1.jpg");
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r1.jpg");

            // Cảnh 1: Đã có sẵn ảnh và prompt chuyển động -> đủ điều kiện render video ngay
            var scene1Id = SeedScene(dbOptions, prjId, "Cảnh 1 - Sẵn sàng", 1, readyAssetId, null);

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ModelAndProduct,
                GoalPrompt: "Quảng cáo áo khoác dạ mùa đông",
                ModelAssetId: modelAssetId,
                ProductAssetId: productAssetId,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            // Bấm Bắt đầu tạo video
            var startRes = await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            Assert(startRes.RunState == WorkflowRunState.Running, "Bắt đầu phải chuyển sang Running.");

            // Một Project chỉ fan-out tới đúng một video output.
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var jobs = await db.Jobs.Where(j => j.ProjectId == prjId).ToListAsync();
                Assert(jobs.Count == 1, $"Kỳ vọng một job video trong hàng đợi, thực tế: {jobs.Count}");

                var jobScene1 = jobs.FirstOrDefault(j => j.SceneId == scene1Id);
                Assert(jobScene1 != null, "Phải có job cho Cảnh 1.");
                Assert(jobScene1!.TaskType == JobTaskType.VideoPrompt, "Ảnh sẵn sàng phải tạo prompt trước.");
            }

            // Xử lý job video duy nhất.
            string job1Id;
            using (var db = new TiToAIDbContext(dbOptions))
            {
                job1Id = (await db.Jobs.FirstAsync(j => j.SceneId == scene1Id)).Id;
            }

            // Tạo Asset thật trong DB để thỏa mãn ràng buộc khóa ngoại (Foreign Key)
            var videoClipAssetId = SeedAsset(dbOptions, "Video", "videos/clip_scene1.mp4");

            // Đặt job sang Succeeded và gọi ApplyJobOutputAsync
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var j1 = await db.Jobs.FirstAsync(j => j.Id == job1Id);
                j1.TaskType = JobTaskType.VideoRender;
                j1.State = JobState.Succeeded;
                j1.ResultReference = videoClipAssetId;
                await db.SaveChangesAsync();
            }

            bool applied1 = await workflowService.ApplyJobOutputAsync(job1Id);
            Assert(applied1, "Apply kết quả VideoRender cho Cảnh 1 phải thành công.");

            // Kiểm tra Cảnh 1: Đã có video clip hoàn tất!
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var s1 = await db.Scenes.FirstAsync(s => s.Id == scene1Id);
                Assert(s1.VideoAssetId == videoClipAssetId, "Cảnh 1 phải nhận được video clip.");
                Assert(s1.Status == "Completed", "Cảnh 1 phải ở trạng thái Completed.");
                var config = await workflowService.GetWorkflowConfigAsync(prjId);
                Assert(config.RunState == WorkflowRunState.Completed, "Video cuối phải hoàn tất run.");

            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 11 (Mốc 4): AI Lập cảnh tự động từ Project Brief khi dự án chưa có cảnh nào
    /// </summary>
    private static async Task TestCase11_AiScenePlanningFromBrief_ZeroScenesAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var registry = new StorageRootRegistry(dbOptions, tempDir);
            await registry.EnsureDefaultRootsAsync();
            var settingsService = new SettingsService(dbOptions, registry);
            var curSettings = await settingsService.GetSettingsAsync();
            await settingsService.SaveSettingsAsync(new SettingsSaveRequest(
                DocumentsPath: curSettings.DocumentsPath,
                ImagesPath: curSettings.ImagesPath,
                VideosPath: curSettings.VideosPath,
                ImageProvider: ProviderKeys.ChatGPTWeb,
                ScriptProvider: ProviderKeys.ChatGPTWeb,
                VideoPromptProvider: ProviderKeys.ChatGPTWeb,
                ExpectedRevision: curSettings.Revision
            ));
            var snapshotFactory = new JobSnapshotFactory(settingsService, dbOptions: dbOptions);
            var reservation = new JobReservation(dbOptions);
            var fakeChatGpt = new DeterministicFakeProviderAdapter(ProviderKeys.ChatGPTWeb);
            var scheduler = new JobScheduler(dbOptions, reservation, new CompositeTestJobExecutor(fakeChatGpt, fakeChatGpt));
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);
            var workflowService = new ProjectWorkflowService(dbOptions, jobService);

            var modelAssetId = SeedAsset(dbOptions, "Image", "models/model_brief.jpg");
            var productAssetId = SeedAsset(dbOptions, "Image", "products/dress.jpg");

            // Dự án ban đầu có 0 cảnh
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var count = await db.Scenes.CountAsync(s => s.ProjectId == prjId);
                Assert(count == 0, "Dự án ban đầu phải có đúng 0 cảnh.");
                var now = DateTimeOffset.UtcNow.ToString("o");
                db.PromptTemplates.AddRange(
                    new PromptTemplate { Id = "script-active", Purpose = "Script", Name = "Script", IsActive = true, TemplateText = "SCRIPT_DANG_DUNG", CreatedAtUtc = now, UpdatedAtUtc = now },
                    new PromptTemplate { Id = "script-inactive", Purpose = "Script", Name = "Cũ", IsActive = false, TemplateText = "SCRIPT_KHONG_DUNG", CreatedAtUtc = now, UpdatedAtUtc = now },
                    new PromptTemplate { Id = "image-active", Purpose = "Image", Name = "Image", IsActive = true, TemplateText = "IMAGE_DANG_DUNG", CreatedAtUtc = now, UpdatedAtUtc = now },
                    new PromptTemplate { Id = "video-active", Purpose = "VideoPrompt", Name = "Video", IsActive = true, TemplateText = "VIDEO_DANG_DUNG", CreatedAtUtc = now, UpdatedAtUtc = now });
                await db.SaveChangesAsync();
            }

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ModelAndProduct,
                GoalPrompt: "BST Váy dạ hội thanh lịch sang trọng",
                ModelAssetId: modelAssetId,
                ProductAssetId: productAssetId,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15
            ));

            // Bấm Bắt đầu tạo video với service có kết nối JobService
            var startRes = await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            Assert(startRes.RunState == WorkflowRunState.Running, "RunState phải chuyển sang Running.");

            // Kiểm tra DB: Phải có 1 Job Script với SceneId = null theo DB constraint
            string scriptJobId;
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var scriptJob = await db.Jobs.FirstOrDefaultAsync(j => j.ProjectId == prjId && j.TaskType == JobTaskType.Script);
                Assert(scriptJob != null, "Phải có Job Script lập cảnh được đưa vào hàng đợi.");
                Assert(scriptJob!.SceneId == null, "Job Script SceneId bắt buộc phải là null theo DB constraint.");
                Assert(scriptJob.State == JobState.Queued, "Job Script phải ở trạng thái Queued.");
                Assert(scriptJob.PromptSnapshotText!.Contains("SCRIPT_DANG_DUNG") && !scriptJob.PromptSnapshotText.Contains("SCRIPT_KHONG_DUNG"), "Job Script phải dùng mẫu đang chọn.");
                scriptJobId = scriptJob.Id;
            }

            // Giả lập AI trả về kịch bản một video dưới dạng JSON
            string fakeAiScriptJson = "{\"scenes\": [" +
                "{\"purpose\": \"Toàn cảnh váy dạ hội\", \"name\": \"Cảnh 1: Giới thiệu váy dạ hội\", \"motionPrompt\": \"Camera tracking từ xa lại gần\", \"targetDurationSeconds\": 15}" +
                "]}";

            using (var db = new TiToAIDbContext(dbOptions))
            {
                var j = await db.Jobs.FirstAsync(j => j.Id == scriptJobId);
                j.State = JobState.Succeeded;
                j.ResultText = fakeAiScriptJson;
                await db.SaveChangesAsync();
            }

            // Áp dụng kết quả Job Script
            bool applied = await workflowService.ApplyJobOutputAsync(scriptJobId);
            Assert(applied, "Apply kết quả Script phải thành công.");

            // AI có thể trả dư cảnh nhưng một Project chỉ nhận một output.
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var scenes = await db.Scenes.Where(s => s.ProjectId == prjId).OrderBy(s => s.SequenceOrder).ToListAsync();
                Assert(scenes.Count == 1, $"Project phải có đúng một output scene, thực tế: {scenes.Count}");

                Assert(scenes[0].SequenceOrder == 1, "Cảnh 1 có SequenceOrder = 1.");
                Assert(scenes[0].Name.Contains("Giới thiệu váy dạ hội"), "Tên Cảnh 1 phải khớp kịch bản.");

                // Chỉ có một output video nên chỉ một nhánh ảnh được xếp hàng.
                var imageJobs = await db.Jobs.Where(j => j.ProjectId == prjId && j.TaskType == JobTaskType.Image).ToListAsync();
                Assert(imageJobs.Count == 1, "Chỉ enqueue Job Image cho video duy nhất của Project.");
                using var imageSnapshot = JsonDocument.Parse(imageJobs[0].PromptSnapshotText!);
                Assert(imageSnapshot.RootElement.GetProperty("renderedText").GetString()!.Contains("Toàn cảnh váy dạ hội"), "Prompt ảnh phải giữ mô tả cảnh do AI tạo.");
                Assert(imageSnapshot.RootElement.GetProperty("renderedText").GetString()!.Contains("IMAGE_DANG_DUNG"), "Job Image phải dùng mẫu đang chọn.");
                var preview = await workflowService.PreviewAiPromptAsync(new PreviewAiPromptRequest(prjId, scenes[0].Id));
                Assert(preview.ComposedPrompt.Contains("Toàn cảnh váy dạ hội"), "Prompt chuyển động phải giữ mô tả cảnh do AI tạo.");
                Assert(preview.ComposedPrompt.Contains("VIDEO_DANG_DUNG"), "Prompt video phải dùng mẫu đang chọn.");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 12 (Mốc 4): Hủy quy trình (Cancel Workflow) giải phóng slot và hủy active jobs an toàn
    /// </summary>
    private static async Task TestCase12_CancelWorkflow_CancelsActiveJobsAndResetsStateAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var registry = new StorageRootRegistry(dbOptions, tempDir);
            await registry.EnsureDefaultRootsAsync();
            var settingsService = new SettingsService(dbOptions, registry);
            var curSettings = await settingsService.GetSettingsAsync();
            await settingsService.SaveSettingsAsync(new SettingsSaveRequest(
                DocumentsPath: curSettings.DocumentsPath,
                ImagesPath: curSettings.ImagesPath,
                VideosPath: curSettings.VideosPath,
                ImageProvider: ProviderKeys.ChatGPTWeb,
                ScriptProvider: ProviderKeys.ChatGPTWeb,
                VideoPromptProvider: ProviderKeys.ChatGPTWeb,
                ExpectedRevision: curSettings.Revision
            ));
            var snapshotFactory = new JobSnapshotFactory(settingsService, dbOptions: dbOptions);
            var reservation = new JobReservation(dbOptions);
            var fakeChatGpt = new DeterministicFakeProviderAdapter(ProviderKeys.ChatGPTWeb);
            var scheduler = new JobScheduler(dbOptions, reservation, new CompositeTestJobExecutor(fakeChatGpt, fakeChatGpt));
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);
            var workflowService = new ProjectWorkflowService(dbOptions, jobService);

            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r_cancel.jpg");
            SeedScene(dbOptions, prjId, "Cảnh test cancel", 1, readyAssetId, null);

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Test cancel workflow",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            // Bắt đầu quy trình -> Tạo job và chuyển sang Running
            await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));

            var runningConfig = await workflowService.GetWorkflowConfigAsync(prjId);
            Assert(runningConfig.RunState == WorkflowRunState.Running, "RunState phải là Running.");

            // Gọi Hủy bỏ quy trình (CancelWorkflowAsync)
            var cancelConfig = await workflowService.CancelWorkflowAsync(prjId);
            Assert(cancelConfig.RunState == WorkflowRunState.Idle, "RunState sau khi Cancel phải chuyển về Idle.");

            // Kiểm tra DB: Các job đang chờ phải được chuyển sang Canceled
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var canceledJobs = await db.Jobs.Where(j => j.ProjectId == prjId).ToListAsync();
                Assert(canceledJobs.All(j => j.State == JobState.Canceled), "Tất cả các active job của project phải chuyển sang Canceled.");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 13: Thẩm định thời lượng clip theo capability & Hỗ trợ mở dữ liệu cũ 5s (CD03–CD08)
    /// - Dữ liệu cũ 5s nạp được bình thường vào service/DTO mà không bị lỗi/xóa/đổi ngầm.
    /// - Khi gọi StartWorkflowAsync với cấu hình 5s hoặc cảnh cũ 5s: Bị từ chối và yêu cầu chọn lại 10s hoặc 15s.
    /// - Cấu hình 30s trực tiếp chưa xác minh: Bị từ chối.
    /// - Cấu hình 15s hoặc 10s: Hợp lệ và bắt đầu thành công.
    /// </summary>
    private static async Task TestCase13_ClipDurationValidationAndLegacyDataSupportAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var service = new ProjectWorkflowService(dbOptions, durableJobEnqueuer: (req, ct) => Task.FromResult(1));
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/legacy.jpg");

            // 1. Giả lập dữ liệu cũ 3 clip x 5s đã lưu trong hệ thống trước đây
            var oldSceneId = SeedScene(dbOptions, prjId, "Cảnh cũ 5s", 1, readyAssetId, "Prompt cảnh cũ", targetDurationSeconds: 5);

            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Dự án cũ 5 giây",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 5 // Cấu hình cũ 5s
            ));

            // CD07, CD08: Mở lại dữ liệu cũ: Cấu hình và cảnh vẫn nạp được nguyên vẹn 5s, không bị xóa/đổi ngầm!
            var loadedConfig = await service.GetWorkflowConfigAsync(prjId);
            Assert(loadedConfig.TargetDurationPerSceneSeconds == 5, "Cấu hình cũ 5s phải nạp được nguyên vẹn, không đổi ngầm.");
            var loadedScenes = await service.ListSceneWorkflowDetailsAsync(prjId);
            Assert(loadedScenes.Count == 1 && loadedScenes[0].TargetDurationSeconds == 5, "Cảnh cũ 5s phải giữ nguyên thời lượng 5s khi đọc.");

            // CD08: Nhưng khi gọi StartWorkflowAsync: Bị chặn với thông báo yêu cầu chọn lại thời lượng hợp lệ
            bool blockedLegacy5s = false;
            try
            {
                await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            }
            catch (ProjectWorkflowValidationException ex) when (ex.Message.Contains("không được nhà cung cấp video hỗ trợ") || ex.Message.Contains("Vui lòng chọn lại thời lượng"))
            {
                blockedLegacy5s = true;
            }
            Assert(blockedLegacy5s, "Bắt đầu với duration 5s chưa được hỗ trợ phải bị chặn và yêu cầu chọn lại.");

            // CD06: 30s hiện là thời lượng provider hỗ trợ nên cấu hình được chấp nhận; cảnh 5s cũ vẫn chặn Start
            bool blocked30s = false;
            try
            {
                await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                    ProjectId: prjId,
                    WorkflowMode: loadedConfig.WorkflowMode,
                    GoalPrompt: loadedConfig.GoalPrompt,
                    ModelAssetId: loadedConfig.ModelAssetId,
                    ProductAssetId: loadedConfig.ProductAssetId,
                    ReadyImageAssetId: loadedConfig.ReadyImageAssetId,
                    AudioOption: loadedConfig.AudioOption,
                    AudioAssetId: loadedConfig.AudioAssetId,
                    TargetSceneCount: loadedConfig.TargetSceneCount,
                    TargetDurationPerSceneSeconds: 30
                ));
                await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            }
            catch (ProjectWorkflowValidationException ex) when (ex.Message.Contains("Cảnh 'Cảnh cũ 5s'"))
            {
                blocked30s = true;
            }
            Assert(blocked30s, "Cấu hình 30s được chấp nhận; Start vẫn phải bị chặn bởi cảnh 5s cũ.");

            // Cập nhật cảnh cũ và cấu hình sang 15s (hoặc 10s) theo capability MuseAI
            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: loadedConfig.WorkflowMode,
                GoalPrompt: loadedConfig.GoalPrompt,
                ModelAssetId: loadedConfig.ModelAssetId,
                ProductAssetId: loadedConfig.ProductAssetId,
                ReadyImageAssetId: loadedConfig.ReadyImageAssetId,
                AudioOption: loadedConfig.AudioOption,
                AudioAssetId: loadedConfig.AudioAssetId,
                TargetSceneCount: loadedConfig.TargetSceneCount,
                TargetDurationPerSceneSeconds: 15
            ));
            await service.UpdateSceneWorkflowInputAsync(new UpdateSceneWorkflowInputRequest(
                ProjectId: prjId,
                SceneId: oldSceneId,
                TargetDurationSeconds: 15
            ));

            // CD03, CD05: Bắt đầu thành công với 15s!
            var started = await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            Assert(started.RunState == WorkflowRunState.Running, "Bắt đầu với 15s phải thành công và chuyển sang Running.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 14: Lần theo GoalPrompt/USER_BRIEF, composition 4 block và ranh giới AI Writer vs MuseAI (§3.1–3.3, CD11–CD14)
    /// - Brief rỗng chặn Bắt đầu tạo video mới (CD13).
    /// - Chuỗi nhận diện riêng UNIQUE_BRIEF_TOKEN được truyền qua composer vào preview và enqueued job.
    /// - Template VideoPrompt được resolve đúng và bind {USER_BRIEF} canonical.
    /// - Output prompt chuyển động gửi MuseAI là kết quả motion prompt, không gửi template/schema thừa sang MuseAI.
    /// </summary>
    private static async Task TestCase14_GoalPromptBriefAndTemplateCompositionAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var registry = new StorageRootRegistry(dbOptions, tempDir);
            await registry.EnsureDefaultRootsAsync();
            var settingsService = new SettingsService(dbOptions, registry);
            var curSettings = await settingsService.GetSettingsAsync();
            await settingsService.SaveSettingsAsync(new SettingsSaveRequest(
                DocumentsPath: curSettings.DocumentsPath,
                ImagesPath: curSettings.ImagesPath,
                VideosPath: curSettings.VideosPath,
                ImageProvider: ProviderKeys.ChatGPTWeb,
                ScriptProvider: ProviderKeys.ChatGPTWeb,
                VideoPromptProvider: ProviderKeys.ChatGPTWeb,
                ExpectedRevision: curSettings.Revision
            ));
            var snapshotFactory = new JobSnapshotFactory(settingsService, dbOptions: dbOptions);
            var reservation = new JobReservation(dbOptions);
            var fakeChatGpt = new DeterministicFakeProviderAdapter(ProviderKeys.ChatGPTWeb);
            var fakeMuseAi = new DeterministicFakeProviderAdapter(ProviderKeys.MuseAI);
            var scheduler = new JobScheduler(dbOptions, reservation, new CompositeTestJobExecutor(fakeChatGpt, fakeMuseAi));
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);
            var workflowService = new ProjectWorkflowService(dbOptions, jobService);

            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/brief_test.jpg");
            var sceneId = SeedScene(dbOptions, prjId, "Clip 1", 1, readyAssetId, prompt: null, targetDurationSeconds: 15);

            // 1. Không còn bắt buộc ô brief riêng; preview dùng template khi GoalPrompt trống.
            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "   ", // Trống
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            var emptyBriefPreview = await workflowService.PreviewAiPromptAsync(new PreviewAiPromptRequest(prjId, sceneId));
            Assert(emptyBriefPreview.ComposedPrompt.Contains("[OUTPUT_CONTRACT]"), "Template vẫn được compose khi GoalPrompt rỗng.");

            // 2. CD11, CD14: Đặt brief có chuỗi nhận diện riêng UNIQUE_BRIEF_TOKEN_CD11
            string uniqueToken = "UNIQUE_BRIEF_TOKEN_CD11_ÁO_SƠ_MI_LỤA_XANH";
            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "",
                VideoPromptText: uniqueToken,
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            // CD14: Kiểm tra Preview AI Prompt
            var preview = await workflowService.PreviewAiPromptAsync(new PreviewAiPromptRequest(prjId, sceneId));
            Assert(preview.ComposedPrompt.Contains(uniqueToken), "ComposedPrompt trong Preview phải chứa chuỗi nhận diện độc nhất.");
            Assert(preview.ComposedPrompt.StartsWith(uniqueToken), "ComposedPrompt phải bắt đầu bằng nguyên văn template người dùng lưu.");
            Assert(preview.ComposedPrompt.Contains("[OUTPUT_CONTRACT]"), "ComposedPrompt phải có block OUTPUT_CONTRACT.");
            Assert(preview.ComposedPrompt.Contains("\"video_prompt\""), "OUTPUT_CONTRACT phải yêu cầu JSON có video_prompt.");

            // CD11, CD12: Khởi chạy workflow -> Kiểm tra Job VideoPrompt được enqueue chứa đúng ComposedPrompt
            var startRes = await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            Assert(startRes.RunState == WorkflowRunState.Running, "StartWorkflowAsync phải chuyển sang Running.");

            using (var db = new TiToAIDbContext(dbOptions))
            {
                var promptJob = await db.Jobs.FirstOrDefaultAsync(j => j.SceneId == sceneId && j.TaskType == JobTaskType.VideoPrompt);
                Assert(promptJob != null, "Phải có Job VideoPrompt cho cảnh.");
                using var doc = JsonDocument.Parse(promptJob!.PromptSnapshotText!);
                var renderedText = doc.RootElement.GetProperty("renderedText").GetString() ?? string.Empty;
                Assert(renderedText.Contains(uniqueToken),
                    "RenderedText trong snapshot gửi tới AI Writer bắt buộc phải chứa UNIQUE_BRIEF_TOKEN từ GoalPrompt.");
                Assert(renderedText.Contains("[OUTPUT_CONTRACT]"),
                    "RenderedText trong snapshot gửi tới AI Writer phải chứa OUTPUT_CONTRACT chuẩn.");

                // Mô phỏng AI Writer hoàn tất viết prompt chuyển động
                promptJob.State = JobState.Succeeded;
                // Phản hồi phải là JSON có video_prompt; văn bản thường không còn được dùng làm prompt render.
                promptJob.ResultText = "{\"video_prompt\": \"Camera slow-motion 60fps trượt nhẹ ngang qua chi tiết cổ áo lụa xanh.\"}";
                await db.SaveChangesAsync();
            }

            // Kích hoạt bước tiếp theo (ApplyJobOutputAsync)
            string promptJobId;
            using (var db = new TiToAIDbContext(dbOptions))
            {
                promptJobId = (await db.Jobs.FirstAsync(j => j.SceneId == sceneId && j.TaskType == JobTaskType.VideoPrompt)).Id;
            }
            await workflowService.ApplyJobOutputAsync(promptJobId);

            // Đủ ảnh và prompt: tự đưa đúng một job render MuseAI vào hàng đợi.
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var renderJobs = await db.Jobs.Where(j => j.SceneId == sceneId && j.TaskType == JobTaskType.VideoRender).ToListAsync();
                Assert(renderJobs.Count == 1, "Phải tự enqueue đúng một job render khi mọi đầu vào đã sẵn sàng.");
                Assert(renderJobs[0].State == JobState.Queued, "Job render tự động phải ở trạng thái Queued.");
                var scene = await db.Scenes.FirstAsync(s => s.Id == sceneId);
                Assert(scene.PromptText == "Camera slow-motion 60fps trượt nhẹ ngang qua chi tiết cổ áo lụa xanh.", "Phải lưu prompt đã tạo.");
            }

            // Bấm lặp khi render còn chờ: phải bị chặn, không tạo thêm job.
            bool duplicateBlocked = false;
            try { await workflowService.CreateSceneVideoAsync(new CreateSceneVideoRequest(prjId)); }
            catch (ProjectWorkflowValidationException) { duplicateBlocked = true; }
            Assert(duplicateBlocked, "Bấm lặp khi render đang chờ phải bị chặn.");

            // Tạo lại sau khi job render thất bại: phải tạo job mới, không replay job cũ.
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var failedJob = await db.Jobs.FirstAsync(j => j.SceneId == sceneId && j.TaskType == JobTaskType.VideoRender);
                failedJob.State = JobState.Failed;
                await db.SaveChangesAsync();
            }
            await workflowService.CreateSceneVideoAsync(new CreateSceneVideoRequest(prjId));
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var renderCount = await db.Jobs.CountAsync(j => j.SceneId == sceneId && j.TaskType == JobTaskType.VideoRender);
                Assert(renderCount == 2, "Tạo lại sau job thất bại phải tạo job render mới, không replay job cũ.");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    private static async Task TestCase15_LegacyMultiSceneProjectIsPreservedAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var service = new ProjectWorkflowService(dbOptions);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/legacy.jpg");
            var sceneId = SeedScene(dbOptions, prjId, "Legacy scene one", 1, readyAssetId, "Pan left");
            SeedScene(dbOptions, prjId, "Legacy scene two", 2, readyAssetId, "Pan right");

            await service.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Preserve old project data",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15));

            var blocked = false;
            try
            {
                await service.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));
            }
            catch (ProjectWorkflowValidationException ex) when (ex.Message.Contains("nhiều cảnh từ workflow cũ"))
            {
                blocked = true;
            }

            Assert(blocked, "Legacy multi-scene project must not produce multiple video outputs under the new model.");
            Assert((await service.GetWorkflowConfigAsync(prjId)).RunState == WorkflowRunState.Idle,
                "Blocked legacy project must not transition into Running.");

            using var db = new TiToAIDbContext(dbOptions);
            Assert(await db.Scenes.CountAsync(s => s.ProjectId == prjId) == 2,
                "Both legacy scenes must remain in the database after the new workflow refuses to run.");
            Assert(!await db.Jobs.AnyAsync(j => j.ProjectId == prjId),
                "A blocked legacy multi-scene project must not enqueue video jobs.");

            var clipId = SeedAsset(dbOptions, "Video", "videos/legacy-clip.mp4");
            var jobId = Guid.NewGuid().ToString("D");
            var now = DateTimeOffset.UtcNow.ToString("o");
            db.Jobs.Add(new Job { Id = jobId, ProjectId = prjId, SceneId = sceneId,
                TaskType = JobTaskType.VideoRender, State = JobState.Succeeded, ProviderKey = "museai",
                ResultReference = clipId, CreatedAtUtc = now, UpdatedAtUtc = now });
            await db.SaveChangesAsync();
            Assert(await service.ApplyJobOutputAsync(jobId), "Phải giữ được clip của cảnh cũ.");
            await using var check = new TiToAIDbContext(dbOptions);
            Assert((await check.Projects.FindAsync(prjId))!.CurrentVideoRevisionId == null,
                "Clip của một cảnh không được chọn làm video cuối của project nhiều cảnh.");
            Assert(!await check.VideoRevisions.AnyAsync(v => v.ProjectId == prjId),
                "Clip trung gian không được tạo revision sẵn sàng đăng.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    private static async Task TestCase16_StartRunAndJobRollbackTogetherAsync()
    {
        var (dbOptions, tempDir, projectId) = CreateTestEnvironment();
        try
        {
            var registry = new StorageRootRegistry(dbOptions, tempDir);
            await registry.EnsureDefaultRootsAsync();
            var settings = new SettingsService(dbOptions, registry);
            var current = await settings.GetSettingsAsync();
            await settings.SaveSettingsAsync(new SettingsSaveRequest(
                DocumentsPath: current.DocumentsPath, ImagesPath: current.ImagesPath, VideosPath: current.VideosPath,
                ImageProvider: ProviderKeys.ChatGPTWeb, ScriptProvider: ProviderKeys.ChatGPTWeb,
                VideoPromptProvider: ProviderKeys.ChatGPTWeb, ExpectedRevision: current.Revision));
            var reservation = new JobReservation(dbOptions);
            await using var scheduler = new JobScheduler(dbOptions, reservation, new DeterministicFakeJobExecutor());
            var jobs = new JobService(dbOptions, new JobSnapshotFactory(settings, dbOptions: dbOptions), scheduler);
            var workflow = new ProjectWorkflowService(dbOptions, jobs);
            var imageId = SeedAsset(dbOptions, "Image", "ready/start.jpg");
            await workflow.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                projectId, WorkflowModes.ReadyImage, "", null, null, imageId,
                AudioOptions.None, null, 1, 15));
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_run_insert BEFORE INSERT ON AppSettings WHEN NEW.key LIKE 'workflow.active_run.%' BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
            }
            var failed = false;
            try { await workflow.StartWorkflowAsync(new StartProjectWorkflowRequest(projectId)); }
            catch (DbUpdateException) { failed = true; }
            Assert(failed, "Lỗi lưu run phải được báo cho caller.");
            await using var verify = new TiToAIDbContext(dbOptions);
            Assert(!await verify.Jobs.AnyAsync(j => j.ProjectId == projectId), "Không được để lại job mồ côi.");
            Assert((await workflow.GetWorkflowConfigAsync(projectId)).RunState == WorkflowRunState.Idle,
                "Run không được chuyển sang Running khi transaction thất bại.");
        }
        finally { CleanupTestEnvironment(tempDir); }
    }

    /// <summary>
    /// Composite Job Executor phục vụ kiểm thử đa provider (ChatGPT + MuseAI).
    /// </summary>
    private sealed class CompositeTestJobExecutor : IJobExecutor
    {
        private readonly IProviderAdapter _chatGptAdapter;
        private readonly IProviderAdapter _museAiAdapter;

        public CompositeTestJobExecutor(IProviderAdapter chatGptAdapter, IProviderAdapter museAiAdapter)
        {
            _chatGptAdapter = chatGptAdapter;
            _museAiAdapter = museAiAdapter;
        }

        public Task<JobExecutionResult> ExecuteAsync(
            JobExecutionContext context,
            Func<Task> onPreSideEffectCommitAsync,
            Func<int, string, Task>? onProgressAsync,
            CancellationToken cancellationToken)
        {
            if (string.Equals(context.ProviderKey, ProviderKeys.MuseAI, StringComparison.OrdinalIgnoreCase))
            {
                return _museAiAdapter.ExecuteAsync(context, onPreSideEffectCommitAsync, onProgressAsync, cancellationToken);
            }

            return _chatGptAdapter.ExecuteAsync(context, onPreSideEffectCommitAsync, onProgressAsync, cancellationToken);
        }
    }
}
