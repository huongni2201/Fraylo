using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Content;
using Fraylo.Application.Jobs;
using Fraylo.Application.Providers;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;
using Fraylo.Contracts.Settings;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử hồi quy tái hiện và kiểm chứng 5 lỗi độc lập (PV-R3, PV-R4, PV-R5) 
/// cùng lỗi mất USER_BRIEF (PV-R11) theo docs/reviews/2026-10-27-audit-pv-independent-recheck.md
/// và docs/plans/2026-10-27-pv-remediation-fix-plan.md.
/// </summary>
public static class PvRemediationRegressionTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] {message}");
        }
    }

    private static (DbContextOptions<FrayloDbContext> dbOptions, string tempDir, string prjId) CreateTestEnvironment()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "titoai-pvrepro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "pvrepro.db");
        var dbOptions = new DbContextOptionsBuilder<FrayloDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var prjId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("o");

        using (var db = new FrayloDbContext(dbOptions))
        {
            db.Database.EnsureCreated();
            VideoWorkflowSmokeTests.SeedRequiredPromptTemplates(db);


            db.Projects.Add(new Project
            {
                Id = prjId,
                Name = "Project Test PV",
                Status = "Draft",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

            db.SaveChanges();
        }

        return (dbOptions, tempDir, prjId);
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

    private static string SeedAsset(DbContextOptions<FrayloDbContext> dbOptions, string assetType, string relativePath)
    {
        using var db = new FrayloDbContext(dbOptions);
        var assetId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("o");

        db.Assets.Add(new Asset
        {
            Id = assetId,
            ProjectId = null,
            StorageRootId = "root-1",
            RelativePath = relativePath,
            AssetType = assetType,
            ByteSize = 1024,
            Sha256Hash = "hash-" + Guid.NewGuid().ToString("N"),
            Status = "Linked",
            CreatedAtUtc = now
        });

        db.SaveChanges();
        return assetId;
    }

    private static async Task<(SettingsService settings, JobService jobs, ProjectWorkflowService workflow)> SetupServicesAsync(
        DbContextOptions<FrayloDbContext> dbOptions, string tempDir)
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

        return (settingsService, jobService, workflowService);
    }

    /// <summary>
    /// Ca 1: PV-R3 — Apply Script lặp phải Idempotent, không nhân đôi cảnh (2 rồi 4 cảnh)
    /// </summary>
    public static async Task Test_Repro1_DuplicateScriptApply_MustBeIdempotentAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, jobService, workflowService) = await SetupServicesAsync(dbOptions, tempDir);

            var modelAssetId = SeedAsset(dbOptions, "Image", "models/m1.jpg");
            var productAssetId = SeedAsset(dbOptions, "Image", "products/p1.jpg");

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ModelAndProduct,
                GoalPrompt: "BST Áo khoác mùa đông",
                ModelAssetId: modelAssetId,
                ProductAssetId: productAssetId,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15,
                VideoStrategy: VideoStrategies.MultiVideo
            ));

            await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));

            string scriptJobId;
            using (var db = new FrayloDbContext(dbOptions))
            {
                var scriptJob = await db.Jobs.FirstAsync(j => j.ProjectId == prjId && j.TaskType == JobTaskType.Script);
                scriptJob.State = JobState.Succeeded;
                scriptJob.ResultText = "{\"scenes\": [" +
                    "{\"name\": \"Cảnh 1: Giới thiệu\", \"imagePrompt\": \"Toàn thân\", \"videoPrompt\": \"Camera tiến dần\"}" +
                    "]}";
                await db.SaveChangesAsync();
                scriptJobId = scriptJob.Id;
            }

            // Apply lần 1: tạo 2 cảnh
            bool firstApplied = await workflowService.ApplyJobOutputAsync(scriptJobId);
            Assert(firstApplied, "Apply lần 1 phải thành công.");

            int firstCount;
            using (var db = new FrayloDbContext(dbOptions))
            {
                firstCount = await db.Scenes.CountAsync(s => s.ProjectId == prjId);
                Assert(firstCount == 1, $"Sau lần 1 phải có đúng 1 cảnh, thực tế: {firstCount}");
            }

            // Apply lần 2: phải idempotent, KHÔNG được tạo thêm cảnh (2 -> 4)
            bool secondApplied = await workflowService.ApplyJobOutputAsync(scriptJobId);
            Assert(secondApplied, "Apply lần 2 (lặp) phải xử lý an toàn (idempotent).");

            int secondCount;
            using (var db = new FrayloDbContext(dbOptions))
            {
                secondCount = await db.Scenes.CountAsync(s => s.ProjectId == prjId);
                Assert(secondCount == 1, $"[PV-R3] Lỗi apply Script lặp: số cảnh bị nhân bản từ {firstCount} thành {secondCount} (kỳ vọng vẫn giữ nguyên 1).");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Ca 2: PV-R3 — Output stale không được ghi đè bản sửa tay của người dùng
    /// </summary>
    public static async Task Test_Repro2_StaleOutput_MustNotOverwriteUserEditedPromptAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, jobService, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r1.jpg");

            string sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToString("o");
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Scenes.Add(new Scene
                {
                    Id = sceneId,
                    ProjectId = prjId,
                    SequenceOrder = 1,
                    Name = "Cảnh 1",
                    TargetDurationSeconds = 15,
                    ImageAssetId = readyAssetId,
                    PromptText = "PROMPT GỐC CŨ",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Prompt test stale",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            // Giả lập một Job VideoPrompt cũ sinh kết quả "OLD AI PROMPT"
            string staleJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = staleJobId,
                    ProjectId = prjId,
                    SceneId = sceneId,
                    TaskType = JobTaskType.VideoPrompt,
                    State = JobState.Succeeded,
                    ProviderKey = ProviderKeys.ChatGPTWeb,
                    StorageRootId = "root-1",
                    PromptSnapshotText = "snapshot",
                    ConfigSnapshotJson = "{}",
                    ResultText = "OLD AI PROMPT",
                    MaxAttempts = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            // Người dùng chủ động sửa Prompt của cảnh thành "USER EDITED PROMPT"
            using (var db = new FrayloDbContext(dbOptions))
            {
                var s = await db.Scenes.FirstAsync(sc => sc.Id == sceneId);
                s.PromptText = "USER EDITED PROMPT";
                s.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o");
                await db.SaveChangesAsync();
            }

            // Áp dụng kết quả job cũ
            await workflowService.ApplyJobOutputAsync(staleJobId);

            // Kiểm tra: PromptText phải giữ "USER EDITED PROMPT", KHÔNG được bị đè thành "OLD AI PROMPT"
            using (var db = new FrayloDbContext(dbOptions))
            {
                var s = await db.Scenes.FirstAsync(sc => sc.Id == sceneId);
                Assert(s.PromptText == "USER EDITED PROMPT",
                    $"[PV-R3] Lỗi apply output cũ: Prompt của người dùng đã bị đè thành '{s.PromptText}' (kỳ vọng bảo toàn 'USER EDITED PROMPT').");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Ca 3: PV-R4 — CancelWorkflowAsync phải xử lý cả Job đang Running, không bỏ sót
    /// </summary>
    public static async Task Test_Repro3_CancelWorkflow_MustCancelRunningJobsAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, jobService, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r_running.jpg");

            string sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToString("o");
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Scenes.Add(new Scene
                {
                    Id = sceneId,
                    ProjectId = prjId,
                    SequenceOrder = 1,
                    Name = "Cảnh 1",
                    TargetDurationSeconds = 15,
                    ImageAssetId = readyAssetId,
                    PromptText = "Prompt",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Prompt test cancel running",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            // Tạo một job ở trạng thái Running
            string runningJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = runningJobId,
                    ProjectId = prjId,
                    SceneId = sceneId,
                    TaskType = JobTaskType.VideoRender,
                    State = JobState.Running,
                    ProviderKey = ProviderKeys.MuseAI,
                    StorageRootId = "root-1",
                    PromptSnapshotText = "snapshot",
                    ConfigSnapshotJson = "{}",
                    CancelRequested = 0,
                    MaxAttempts = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            // Gọi CancelWorkflowAsync
            await workflowService.CancelWorkflowAsync(prjId);

            // Kiểm tra: Job đang Running phải được yêu cầu hủy (CancelRequested == 1 hoặc State == Canceled)
            using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == runningJobId);
                Assert(job.CancelRequested == 1 || job.State == JobState.Canceled,
                    $"[PV-R4] Lỗi Cancel bỏ sót Running: Job {job.Id} vẫn ở State={job.State}, CancelRequested={job.CancelRequested} (kỳ vọng CancelRequested=1 hoặc Canceled).");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Ca 4: PV-R4 — Khi Workflow đang Paused, ApplyJobOutputAsync KHÔNG được tự ý enqueue bước tiếp theo
    /// </summary>
    public static async Task Test_Repro4_PauseWorkflow_MustPreventEnqueuingNextStepsAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, jobService, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r_pause.jpg");

            string sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToString("o");
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Scenes.Add(new Scene
                {
                    Id = sceneId,
                    ProjectId = prjId,
                    SequenceOrder = 1,
                    Name = "Cảnh 1",
                    TargetDurationSeconds = 15,
                    ImageAssetId = readyAssetId,
                    PromptText = null,
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Prompt test pause",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            // Tạm dừng quy trình (PauseWorkflowAsync)
            await workflowService.PauseWorkflowAsync(prjId);

            int queuedBefore;
            using (var db = new FrayloDbContext(dbOptions))
            {
                queuedBefore = await db.Jobs.CountAsync(j => j.State == JobState.Queued);
            }

            // Tạo một Job VideoPrompt vừa hoàn tất trong thời gian tạm dừng
            string promptJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = promptJobId,
                    ProjectId = prjId,
                    SceneId = sceneId,
                    TaskType = JobTaskType.VideoPrompt,
                    State = JobState.Succeeded,
                    ProviderKey = ProviderKeys.ChatGPTWeb,
                    StorageRootId = "root-1",
                    PromptSnapshotText = "snapshot",
                    ConfigSnapshotJson = "{}",
                    ResultText = "Prompt mới hoàn tất",
                    MaxAttempts = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            // Gọi Apply kết quả của job này
            await workflowService.ApplyJobOutputAsync(promptJobId);

            // Kiểm tra: Vì quy trình đang Paused, KHÔNG được tự ý enqueue Job VideoRender mới vào queue!
            int queuedAfter;
            using (var db = new FrayloDbContext(dbOptions))
            {
                queuedAfter = await db.Jobs.CountAsync(j => j.State == JobState.Queued);
                Assert(queuedAfter == queuedBefore,
                    $"[PV-R4] Lỗi Pause vẫn enqueue: Số job Queued tăng từ {queuedBefore} lên {queuedAfter} khi đang Paused (kỳ vọng không enqueue mới).");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Ca 5: PV-R5 — Cảnh có SCRIPT MOTION DRAFT khi Image thành công không được đứng im
    /// </summary>
    public static async Task Test_Repro5_ImageSuccess_WithScriptMotionDraft_MustProgressToNextStepAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, jobService, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r_img.jpg");

            string sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToString("o");
            using (var db = new FrayloDbContext(dbOptions))
            {
                // Cảnh ban đầu chưa có ảnh, nhưng ĐÃ CÓ draft từ Script: "SCRIPT MOTION DRAFT"
                db.Scenes.Add(new Scene
                {
                    Id = sceneId,
                    ProjectId = prjId,
                    SequenceOrder = 1,
                    Name = "Cảnh 1",
                    TargetDurationSeconds = 15,
                    ImageAssetId = null,
                    PromptText = "SCRIPT MOTION DRAFT",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ModelAndProduct,
                GoalPrompt: "BST Váy dạ hội",
                ModelAssetId: readyAssetId,
                ProductAssetId: readyAssetId,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15,
                // Image -> VideoPrompt chuyển tiếp là đường của luồng một video (PV-R5).
                VideoStrategy: VideoStrategies.SingleVideo
            ));

            await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));

            int jobsBefore;
            using (var db = new FrayloDbContext(dbOptions))
            {
                jobsBefore = await db.Jobs.CountAsync();
            }

            // Tạo Job Image hoàn tất thành công cho cảnh
            string imageJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = imageJobId,
                    ProjectId = prjId,
                    SceneId = sceneId,
                    TaskType = JobTaskType.Image,
                    State = JobState.Succeeded,
                    ProviderKey = ProviderKeys.ChatGPTWeb,
                    StorageRootId = "root-1",
                    PromptSnapshotText = "snapshot",
                    ConfigSnapshotJson = "{}",
                    ResultReference = readyAssetId,
                    MaxAttempts = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            // Gọi ApplyJobOutputAsync cho Image
            await workflowService.ApplyJobOutputAsync(imageJobId);

            // Kiểm tra: Phải enqueue bước tiếp theo (VideoPrompt hoặc VideoRender), KHÔNG được dừng im với 0 successor job!
            using (var db = new FrayloDbContext(dbOptions))
            {
                int jobsAfter = await db.Jobs.CountAsync();
                int newSuccessors = jobsAfter - jobsBefore - 1; // trừ job Image giả lập
                Assert(newSuccessors > 0,
                    $"[PV-R5] Lỗi Image có draft bị đứng: Sau khi có ảnh, 0 successor job nào được enqueue (kỳ vọng enqueue bước tiếp theo).");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Ca 6: PV-R11 — Chuyển tiếp Image -> VideoPrompt phải truyền USER_BRIEF / GoalPrompt vào successor job
    /// </summary>
    public static async Task Test_Repro6_ImageToVideoPromptSuccessor_MustPreserveUserBriefAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, jobService, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r_brief.jpg");

            string sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToString("o");
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Scenes.Add(new Scene
                {
                    Id = sceneId,
                    ProjectId = prjId,
                    SequenceOrder = 1,
                    Name = "Cảnh 1",
                    TargetDurationSeconds = 15,
                    ImageAssetId = null,
                    PromptText = null,
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            string uniqueBriefToken = "UNIQUE_BRIEF_TOKEN_PV_R11_ÁO_LEN_CASHMERE";
            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ModelAndProduct,
                GoalPrompt: "",
                VideoGuidance: uniqueBriefToken,
                ModelAssetId: readyAssetId,
                ProductAssetId: readyAssetId,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));

            // Tạo Job Image hoàn tất
            string imageJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = imageJobId,
                    ProjectId = prjId,
                    SceneId = sceneId,
                    TaskType = JobTaskType.Image,
                    State = JobState.Succeeded,
                    ProviderKey = ProviderKeys.ChatGPTWeb,
                    StorageRootId = "root-1",
                    PromptSnapshotText = "snapshot",
                    ConfigSnapshotJson = "{}",
                    ResultReference = readyAssetId,
                    MaxAttempts = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            // Gọi Apply cho Job Image -> kích hoạt enqueue VideoPrompt kế tiếp
            await workflowService.ApplyJobOutputAsync(imageJobId);

            // Kiểm tra: Job VideoPrompt kế tiếp bắt buộc phải chứa uniqueBriefToken từ GoalPrompt trong PromptSnapshotText!
            using (var db = new FrayloDbContext(dbOptions))
            {
                var promptJob = await db.Jobs.FirstOrDefaultAsync(j => j.SceneId == sceneId && j.TaskType == JobTaskType.VideoPrompt);
                Assert(promptJob != null, "Phải có Job VideoPrompt được enqueue sau khi Image hoàn tất.");
                Assert(!string.IsNullOrWhiteSpace(promptJob!.PromptSnapshotText), "PromptSnapshotText của VideoPrompt không được rỗng.");

                using var doc = JsonDocument.Parse(promptJob.PromptSnapshotText!);
                var renderedText = doc.RootElement.GetProperty("renderedText").GetString() ?? string.Empty;
                Assert(renderedText.Contains(uniqueBriefToken),
                    $"[PV-R11] Lỗi mất USER_BRIEF: RenderedText '{renderedText}' không chứa '{uniqueBriefToken}' từ GoalPrompt.");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Ca 7: Kết quả tự động phải áp dụng vào Scene và tạo successor job
    /// </summary>
    public static async Task Test_Repro7_AutomaticResult_MustTriggerApplyAndSuccessorJobAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, jobService, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r_automatic.jpg");

            string sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToString("o");
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Scenes.Add(new Scene
                {
                    Id = sceneId,
                    ProjectId = prjId,
                    SequenceOrder = 1,
                    Name = "Cảnh 1",
                    TargetDurationSeconds = 15,
                    ImageAssetId = null,
                    PromptText = null,
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ModelAndProduct,
                GoalPrompt: "BST Hè Rực Rỡ",
                ModelAssetId: readyAssetId,
                ProductAssetId: readyAssetId,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));

            // Kết quả đã được executor lưu thành công, như đầu vào của event job.state_changed.
            string imageJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            long fencingToken = 1;
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = imageJobId,
                    ProjectId = prjId,
                    SceneId = sceneId,
                    TaskType = JobTaskType.Image,
                    State = JobState.Succeeded,
                    ResultReference = readyAssetId,
                    ProviderKey = ProviderKeys.ChatGPTWeb,
                    StorageRootId = "root-1",
                    PromptSnapshotText = "snapshot",
                    ConfigSnapshotJson = "{}",
                    MaxAttempts = 3,
                    FencingToken = fencingToken,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            Assert(await workflowService.ApplyJobOutputAsync(imageJobId), "Kết quả tự động phải được áp dụng.");

            // Kiểm tra: Scene phải được gán ImageAssetId tự động qua hook Apply
            using (var db = new FrayloDbContext(dbOptions))
            {
                var scene = await db.Scenes.FirstAsync(s => s.Id == sceneId);
                Assert(scene.ImageAssetId == readyAssetId, "Cảnh phải nhận ImageAssetId sau khi nhận kết quả tự động.");

                // Kiểm tra: Successor VideoPrompt phải được tự động enqueue!
                var promptJob = await db.Jobs.FirstOrDefaultAsync(j => j.SceneId == sceneId && j.TaskType == JobTaskType.VideoPrompt);
                Assert(promptJob != null, "Sau khi nhận ảnh tự động, phải enqueue successor VideoPrompt.");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Ca 8: PV-R1 — ProductionJobExecutor chưa có browser automation phải báo Failed, KHÔNG fake URI
    /// </summary>
    public static async Task Test_Repro8_ProductionJobExecutor_WithoutBrowser_MustFailAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var registry = new ProviderRegistry();
            var executor = new ProductionJobExecutor(registry);

            var context = new JobExecutionContext(
                JobId: "job-exec-1",
                ProjectId: prjId,
                SceneId: "scene-1",
                TaskType: JobTaskType.VideoRender,
                ProviderKey: ProviderKeys.MuseAI,
                StorageRootId: "root-1",
                PromptSnapshotText: "Góc quay 4K",
                ConfigSnapshotJson: "{}",
                FencingToken: 1,
                Attempts: 1
            );

            var result = await executor.ExecuteAsync(context, () => Task.CompletedTask, null, CancellationToken.None);

            Assert(!result.Success, "Executor không được báo Success khi provider chưa có adapter!");
            Assert(result.ResultReference == null, "Không được tự dựng URI/ResultReference ảo!");
            Assert(string.Equals(result.ReasonCode, "adapter_not_available", StringComparison.OrdinalIgnoreCase),
                $"ReasonCode phải là adapter_not_available, thực tế: {result.ReasonCode}");

            // Không retry hoặc chuyển giao thủ công khi thiếu adapter.
            var dummyJob = new Job
            {
                Id = "job-exec-1",
                ProjectId = prjId,
                State = JobState.Running,
                Attempts = 1,
                MaxAttempts = 3,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
            };

            var decision = JobRetryAndCancelPolicy.EvaluateFailure(dummyJob, result.ReasonCode, false);
            Assert(decision.TargetState == JobState.Failed && !decision.CanRetry,
                $"Job lỗi adapter phải Failed và không retry, thực tế: {decision.TargetState}");

            foreach (var reason in new[] { "automation_blocked", "adapter_not_available", "missing_provider",
                "CAPABILITY_UNSUPPORTED", "manual_input_required", "MANUAL_HANDOFF_REQUIRED" })
            {
                var blocked = JobRetryAndCancelPolicy.EvaluateFailure(dummyJob, reason, false);
                Assert(blocked.TargetState == JobState.Failed && !blocked.CanRetry && blocked.FinalReasonCode == reason,
                    $"{reason}: phải dừng và giữ lý do, không retry/handoff.");
                Assert(!JobRetryAndCancelPolicy.CanRetry(dummyJob, reason, false), "Không retry lỗi automation cố định.");

                var uncertain = JobRetryAndCancelPolicy.EvaluateFailure(dummyJob, reason, true);
                Assert(uncertain.TargetState == JobState.AwaitingUser && uncertain.FinalReasonCode == JobReasonCode.ReconcileRequired,
                    "Đã submit phải ưu tiên đối soát, không Failed rồi gửi lại.");
            }

            var (_, jobs, _) = await SetupServicesAsync(dbOptions, tempDir);
            dummyJob.ProviderKey = ProviderKeys.MuseAI;
            dummyJob.StorageRootId = "root-1";
            dummyJob.TaskType = JobTaskType.Script;
            dummyJob.PromptSnapshotText = "Legacy immutable prompt";
            dummyJob.ConfigSnapshotJson = "{\"legacy\":true}";
            dummyJob.State = JobState.AwaitingUser;
            dummyJob.ReasonCode = "MANUAL_HANDOFF_REQUIRED";
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(dummyJob);
                await db.SaveChangesAsync();
            }

            bool resumeBlocked = false;
            try { await jobs.ResumeAsync(dummyJob.Id); }
            catch (JobValidationException) { resumeBlocked = true; }
            Assert(resumeBlocked, "Job chờ handoff cũ không được tự đưa lại vào queue.");

            using (var db = new FrayloDbContext(dbOptions))
            {
                var legacyJob = await db.Jobs.SingleAsync(j => j.Id == dummyJob.Id);
                legacyJob.State = JobState.Failed;
                await db.SaveChangesAsync();
            }
            bool retryBlocked = false;
            try { await jobs.RetryAsync(new JobRetryRequest(dummyJob.Id, dummyJob.Revision)); }
            catch (JobValidationException) { retryBlocked = true; }
            Assert(retryBlocked, "Backend không retry job thất bại do handoff/automation chưa sẵn sàng.");
            using (var db = new FrayloDbContext(dbOptions))
            {
                var legacyJob = await db.Jobs.SingleAsync(j => j.Id == dummyJob.Id);
                legacyJob.State = JobState.AwaitingUser;
                await db.SaveChangesAsync();
            }

            await jobs.CancelAsync(dummyJob.Id);
            using (var db = new FrayloDbContext(dbOptions))
            {
                var legacyJob = await db.Jobs.SingleAsync(j => j.Id == dummyJob.Id);
                Assert(legacyJob.State == JobState.Canceled, "Vẫn hủy được job handoff lịch sử.");
                Assert(legacyJob.PromptSnapshotText == dummyJob.PromptSnapshotText, "Hủy không mất snapshot lịch sử.");
                Assert(legacyJob.ConfigSnapshotJson == dummyJob.ConfigSnapshotJson, "Hủy không mất cấu hình lịch sử.");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Ca 9: PV-R7/PV-R10 — Quét phục hồi Continuation Intent sau crash ứng dụng
    /// </summary>
    public static async Task Test_Repro9_CrashRecovery_ContinuationSweep_MustResumePendingIntentsAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, jobService, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r_crash.jpg");
            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId, WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Kiểm tra phục hồi run tự động", ModelAssetId: null, ProductAssetId: null,
                ReadyImageAssetId: readyAssetId, AudioOption: AudioOptions.None, AudioAssetId: null,
                TargetSceneCount: 1, TargetDurationPerSceneSeconds: 15));
            var run = await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(prjId));

            string sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToString("o");
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Scenes.Add(new Scene
                {
                    Id = sceneId,
                    ProjectId = prjId,
                    SequenceOrder = 1,
                    Name = "Cảnh 1",
                    TargetDurationSeconds = 15,
                    ImageAssetId = readyAssetId,
                    PromptText = "Prompt đã sẵn sàng",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                // Giả lập intent chuyển tiếp bị treo lại do tiến trình crash giữa SaveChanges và Dispatch
                var intent = new WorkflowContinuationIntent(
                    IntentId: "intent-crash-1",
                    ProjectId: prjId,
                    SceneId: sceneId,
                    TaskType: JobTaskType.VideoRender,
                    CustomPromptText: "Prompt đã sẵn sàng",
                    MediaSnapshot: null,
                    RunId: run.RunId,
                    CreatedAtUtc: now
                );

                db.AppSettings.Add(new AppSetting
                {
                    Key = $"workflow.continuation.{intent.IntentId}",
                    Value = JsonSerializer.Serialize(intent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
                    UpdatedAtUtc = now
                });

                await db.SaveChangesAsync();
            }

            // Gọi sweep phục hồi khi khởi động lại ứng dụng
            int resumedCount = await workflowService.RunWorkflowContinuationSweepAsync();
            Assert(resumedCount == 1, $"Phải phục hồi đúng 1 intent bị treo, thực tế: {resumedCount}");

            // Kiểm tra: Job VideoRender đã được đưa vào queue và intent đã được xóa
            using (var db = new FrayloDbContext(dbOptions))
            {
                var queuedJob = await db.Jobs.FirstOrDefaultAsync(j => j.SceneId == sceneId && j.TaskType == JobTaskType.VideoRender);
                Assert(queuedJob != null, "[PV-R7] Lỗi crash recovery: Tác vụ chuyển tiếp không được enqueue sau sweep.");

                var remainingIntents = await dbContextCountAsync(db);
                Assert(remainingIntents == 0, "Intent sau khi sweep thành công phải được dọn khỏi AppSettings.");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    private static async Task<int> dbContextCountAsync(FrayloDbContext db)
    {
        return await db.AppSettings.CountAsync(s => s.Key.StartsWith("workflow.continuation."));
    }

    /// <summary>
    /// Ca 10: PV-R1 — StartWorkflowAsync Idempotency với ClientRequestId
    /// </summary>
    public static async Task Test_Repro10_StartWorkflow_IdempotencyWithClientRequestIdAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, jobService, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
            var modelAssetId = SeedAsset(dbOptions, "Image", "models/m_idem.jpg");
            var productAssetId = SeedAsset(dbOptions, "Image", "products/p_idem.jpg");

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ModelAndProduct,
                GoalPrompt: "BST Idempotent Test",
                ModelAssetId: modelAssetId,
                ProductAssetId: productAssetId,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15,
                VideoStrategy: VideoStrategies.MultiVideo
            ));

            string clientRequestId ="req-idempotent-" + Guid.NewGuid().ToString("N");

            // Lần 1: Bắt đầu workflow
            var resp1 = await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(
                ProjectId: prjId,
                ClientRequestId: clientRequestId
            ));

            Assert(!string.IsNullOrWhiteSpace(resp1.RunId), "Lần 1 phải sinh RunId.");
            Assert(resp1.RunState == WorkflowRunState.Running, "Lần 1 RunState phải là Running.");

            int scriptJobCount1;
            using (var db = new FrayloDbContext(dbOptions))
            {
                scriptJobCount1 = await db.Jobs.CountAsync(j => j.ProjectId == prjId && j.TaskType == JobTaskType.Script);
                Assert(scriptJobCount1 == 1, $"Lần 1 phải có đúng 1 Script Job, thực tế: {scriptJobCount1}");
            }

            // Lần 2: Gọi lại với cùng clientRequestId (mô phỏng double click hoặc mạng lặp)
            var resp2 = await workflowService.StartWorkflowAsync(new StartProjectWorkflowRequest(
                ProjectId: prjId,
                ClientRequestId: clientRequestId
            ));

            Assert(resp2.RunId == resp1.RunId, $"[PV-R1] Idempotency thất bại: RunId lần 2 ({resp2.RunId}) khác lần 1 ({resp1.RunId}).");

            // Kiểm tra: Tuyệt đối không được enqueue thêm Script Job thứ hai
            using (var db = new FrayloDbContext(dbOptions))
            {
                int scriptJobCount2 = await db.Jobs.CountAsync(j => j.ProjectId == prjId && j.TaskType == JobTaskType.Script);
                Assert(scriptJobCount2 == 1, $"[PV-R1] Trùng lặp: Số Script Job bị tăng lên {scriptJobCount2} sau khi gửi cùng ClientRequestId (kỳ vọng vẫn giữ nguyên 1).");
            }
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// PV-RenderedPromptText: Backend dùng nguyên văn prompt đã render từ frontend.
    /// </summary>
    private static async Task Test_Repro11_RenderedPromptTextAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var workflowService = new ProjectWorkflowService(dbOptions);
            var composer = new VideoWorkflowPromptComposer(dbOptions);
            const string scriptPrompt = "Viết kịch bản về áo dài cho GenZ. Tag: TAG_DAC_BIET_2026.";
            const string videoPrompt = "Camera motion cho Cảnh 1, 15s. Bối cảnh: Sân vườn mùa xuân.";

            var savedConfig = await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ProductOnly,
                GoalPrompt: "Bộ sưu tập áo dài lụa tơ tằm",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15,
                ScriptGuidance: scriptPrompt,
                VideoGuidance: videoPrompt
            ));

            Assert(savedConfig.ScriptGuidance == scriptPrompt, "Hướng dẫn Script của Dự án phải được lưu nguyên vẹn.");
            Assert(savedConfig.VideoGuidance == videoPrompt, "Hướng dẫn VideoPrompt của Dự án phải được lưu nguyên vẹn.");

            var scriptResult = await composer.ComposeScriptPlanningAiRequestAsync(savedConfig);
            Assert(scriptResult.ComposedPrompt.Contains(scriptPrompt), "Composer Script phải dùng nguyên văn prompt của Dự án.");

            var scene = new Scene
            {
                Id = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                ProjectId = prjId,
                SequenceOrder = 1,
                Name = "Cảnh 1",
                TargetDurationSeconds = 15,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
            };
            var videoResult = await composer.ComposeVideoPromptAiRequestAsync(savedConfig, scene);
            Assert(videoResult.ComposedPrompt.Contains(videoPrompt), "Composer VideoPrompt phải dùng nguyên văn prompt của Dự án.");

            // Không còn biến: dấu ngoặc nhọn là văn bản thường, prompt dự án được lưu và dùng nguyên văn.
            const string bracePrompt = "Kịch bản với {ngoac_nhon} giữ nguyên.";
            var braceConfig = await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ProductOnly,
                GoalPrompt: "",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: null,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 2,
                TargetDurationPerSceneSeconds: 15,
                ScriptGuidance: bracePrompt
            ));
            var braceResult = await composer.ComposeScriptPlanningAiRequestAsync(braceConfig);
            Assert(braceResult.ComposedPrompt.Contains(bracePrompt), "Prompt có dấu ngoặc nhọn phải được dùng nguyên văn.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }
    /// <summary>
    /// Ca 12: AI từ chối VideoPrompt khi scene đã có prompt cũ: prompt cũ không được tự render.
    /// </summary>
    public static async Task Test_Repro12_RejectedVideoPrompt_MustNotAutoRenderOldPromptAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (_, _, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
            var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r_reject.jpg");

            string sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToString("o");
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Scenes.Add(new Scene
                {
                    Id = sceneId,
                    ProjectId = prjId,
                    SequenceOrder = 1,
                    Name = "Cảnh 1",
                    TargetDurationSeconds = 15,
                    ImageAssetId = readyAssetId,
                    PromptText = "OLD PROMPT",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
                ProjectId: prjId,
                WorkflowMode: WorkflowModes.ReadyImage,
                GoalPrompt: "Prompt test từ chối",
                ModelAssetId: null,
                ProductAssetId: null,
                ReadyImageAssetId: readyAssetId,
                AudioOption: AudioOptions.None,
                AudioAssetId: null,
                TargetSceneCount: 1,
                TargetDurationPerSceneSeconds: 15
            ));

            // Phản hồi văn bản thường (không phải JSON có video_prompt) bị coi là từ chối.
            string promptJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = promptJobId,
                    ProjectId = prjId,
                    SceneId = sceneId,
                    TaskType = JobTaskType.VideoPrompt,
                    State = JobState.Succeeded,
                    ProviderKey = ProviderKeys.ChatGPTWeb,
                    StorageRootId = "root-1",
                    PromptSnapshotText = "snapshot",
                    ConfigSnapshotJson = "{}",
                    ResultText = "Xin lỗi, tôi không thể tạo nội dung này.",
                    MaxAttempts = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            await workflowService.ApplyJobOutputAsync(promptJobId);

            using var verify = new FrayloDbContext(dbOptions);
            var renderJobs = await verify.Jobs.CountAsync(j => j.ProjectId == prjId && j.TaskType == JobTaskType.VideoRender);
            Assert(renderJobs == 0, $"[PV-R12] AI từ chối nhưng vẫn sinh {renderJobs} job VideoRender bằng prompt cũ.");

            var scene = await verify.Scenes.AsNoTracking().FirstAsync(s => s.Id == sceneId);
            Assert(scene.PromptText == "OLD PROMPT", "[PV-R12] Prompt cũ của cảnh không được bị ghi đè khi AI từ chối.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>Tạo cảnh có prompt cũ rồi cho AI từ chối VideoPrompt; trả về id cảnh và service để kiểm tra tiếp.</summary>
    private static async Task<(string sceneId, ProjectWorkflowService workflow)> ApplyRejectedVideoPromptAsync(
        DbContextOptions<FrayloDbContext> dbOptions, string tempDir, string prjId)
    {
        var (_, _, workflowService) = await SetupServicesAsync(dbOptions, tempDir);
        var readyAssetId = SeedAsset(dbOptions, "Image", "ready/r_reject_manual.jpg");
        string sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("o");
        using (var db = new FrayloDbContext(dbOptions))
        {
            db.Scenes.Add(new Scene
            {
                Id = sceneId, ProjectId = prjId, SequenceOrder = 1, Name = "Cảnh 1", TargetDurationSeconds = 15,
                ImageAssetId = readyAssetId, PromptText = "OLD PROMPT", Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
        }

        await workflowService.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(
            ProjectId: prjId, WorkflowMode: WorkflowModes.ReadyImage, GoalPrompt: "Prompt test từ chối",
            ModelAssetId: null, ProductAssetId: null, ReadyImageAssetId: readyAssetId,
            AudioOption: AudioOptions.None, AudioAssetId: null, TargetSceneCount: 1, TargetDurationPerSceneSeconds: 15));

        string promptJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        using (var db = new FrayloDbContext(dbOptions))
        {
            db.Jobs.Add(new Job
            {
                Id = promptJobId, ProjectId = prjId, SceneId = sceneId, TaskType = JobTaskType.VideoPrompt,
                State = JobState.Succeeded, ProviderKey = ProviderKeys.ChatGPTWeb, StorageRootId = "root-1",
                PromptSnapshotText = "snapshot", ConfigSnapshotJson = "{}",
                ResultText = "Xin lỗi, tôi không thể tạo nội dung này.", MaxAttempts = 3, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
        }

        await workflowService.ApplyJobOutputAsync(promptJobId);
        return (sceneId, workflowService);
    }

    /// <summary>
    /// Ca 13: Sau khi AI từ chối, nút "Tạo video" thủ công không được đưa prompt cũ vào hàng đợi.
    /// </summary>
    public static async Task Test_Repro13_RejectedVideoPrompt_ManualRenderMustNotEnqueueOldPromptAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (sceneId, workflowService) = await ApplyRejectedVideoPromptAsync(dbOptions, tempDir, prjId);

            bool threw = false;
            try
            {
                await workflowService.CreateSceneVideoAsync(new CreateSceneVideoRequest(prjId, SceneId: sceneId));
            }
            catch (ProjectWorkflowValidationException)
            {
                threw = true;
            }

            using var verify = new FrayloDbContext(dbOptions);
            var renderJobs = await verify.Jobs.CountAsync(j => j.ProjectId == prjId && j.TaskType == JobTaskType.VideoRender);
            Assert(threw, "[PV-R13] Render thủ công khi prompt bị từ chối phải bị chặn bằng lỗi nghiệp vụ.");
            Assert(renderJobs == 0, $"[PV-R13] Render thủ công vẫn sinh {renderJobs} job VideoRender bằng prompt cũ.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    /// <summary>
    /// Ca 14: Người dùng lưu prompt hợp lệ thì bỏ cờ lỗi prompt nhưng giữ cờ video cần tạo lại.
    /// </summary>
    public static async Task Test_Repro14_ManualValidPrompt_MustClearPromptStaleFlagAsync()
    {
        var (dbOptions, tempDir, prjId) = CreateTestEnvironment();
        try
        {
            var (sceneId, workflowService) = await ApplyRejectedVideoPromptAsync(dbOptions, tempDir, prjId);

            await workflowService.UpdateVideoContentItemAsync(new UpdateVideoContentItemRequest(
                ProjectId: prjId, Id: sceneId, VideoPrompt: "NEW VALID PROMPT"));

            using var verify = new FrayloDbContext(dbOptions);
            var setting = await verify.AppSettings.AsNoTracking().FirstAsync(s => s.Key == $"scene.workflow.{sceneId}");
            var meta = JsonSerializer.Deserialize<SceneWorkflowDetailDto>(setting.Value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var scene = await verify.Scenes.AsNoTracking().FirstAsync(s => s.Id == sceneId);

            Assert(scene.PromptText == "NEW VALID PROMPT", "[PV-R14] Prompt hợp lệ do người dùng lưu phải được ghi vào cảnh.");
            Assert(meta.StaleTaskTypes?.Contains(JobTaskType.VideoPrompt) != true, "[PV-R14] Lưu prompt hợp lệ phải xóa cờ lỗi VideoPrompt.");
            Assert(meta.StaleTaskTypes?.Contains(JobTaskType.VideoRender) == true, "[PV-R14] Lưu prompt phải giữ cờ video cần tạo lại.");
        }
        finally
        {
            CleanupTestEnvironment(tempDir);
        }
    }

    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("\n=================================================================");
        Console.WriteLine("    Fraylo — PV Remediation Regression & Bug Reproduction Suite  ");
        Console.WriteLine("=================================================================");

        var tests = new (string Name, Func<Task> TestFunc)[]
        {
            ("PV-R3 Ca 1: Apply Script lặp phải Idempotent, không nhân đôi cảnh", Test_Repro1_DuplicateScriptApply_MustBeIdempotentAsync),
            ("PV-R3 Ca 2: Output stale không được đè bản sửa tay của người dùng", Test_Repro2_StaleOutput_MustNotOverwriteUserEditedPromptAsync),
            ("PV-R4 Ca 3: CancelWorkflowAsync phải xử lý cả Job đang Running", Test_Repro3_CancelWorkflow_MustCancelRunningJobsAsync),
            ("PV-R4 Ca 4: Khi Paused, ApplyJobOutputAsync không được tự enqueue tiếp", Test_Repro4_PauseWorkflow_MustPreventEnqueuingNextStepsAsync),
            ("PV-R5 Ca 5: Image thành công với Script Motion Draft không được đứng im", Test_Repro5_ImageSuccess_WithScriptMotionDraft_MustProgressToNextStepAsync),
            ("PV-R11 Ca 6: Image -> VideoPrompt chuyển tiếp phải giữ USER_BRIEF", Test_Repro6_ImageToVideoPromptSuccessor_MustPreserveUserBriefAsync),
            ("Ca 7: Kết quả tự động phải trigger Apply và successor", Test_Repro7_AutomaticResult_MustTriggerApplyAndSuccessorJobAsync),
            ("PV-R1 Ca 8: ProductionJobExecutor chưa có browser automation phải Failed", Test_Repro8_ProductionJobExecutor_WithoutBrowser_MustFailAsync),
            ("PV-R7/10 Ca 9: Crash Recovery continuation sweep phải phục hồi intent bị treo", Test_Repro9_CrashRecovery_ContinuationSweep_MustResumePendingIntentsAsync),
            ("PV-R1 Ca 10: StartWorkflowAsync Idempotency với ClientRequestId", Test_Repro10_StartWorkflow_IdempotencyWithClientRequestIdAsync),
            ("PV-RenderedPromptText Ca 11: Dùng text đã render cho Template Script/VideoPrompt", Test_Repro11_RenderedPromptTextAsync),
            ("PV-R12 Ca 12: AI từ chối VideoPrompt không được render lại bằng prompt cũ", Test_Repro12_RejectedVideoPrompt_MustNotAutoRenderOldPromptAsync),
            ("PV-R13 Ca 13: Render thủ công sau khi AI từ chối không được dùng prompt cũ", Test_Repro13_RejectedVideoPrompt_ManualRenderMustNotEnqueueOldPromptAsync),
            ("PV-R14 Ca 14: Lưu prompt hợp lệ phải xóa cờ lỗi prompt, giữ cờ render", Test_Repro14_ManualValidPrompt_MustClearPromptStaleFlagAsync),
        };

        int pass = 0;
        int fail = 0;

        foreach (var (name, testFunc) in tests)
        {
            Console.Write($"[RUN] {name} ... ");
            try
            {
                await testFunc();
                Console.WriteLine("PASS");
                pass++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {ex.Message}");
                fail++;
            }
        }

        Console.WriteLine("\n-----------------------------------------------------------------");
        Console.WriteLine($"Kết quả kiểm thử PV Remediation: {pass} PASS, {fail} FAIL.");
        Console.WriteLine("=================================================================\n");

        return fail == 0 ? 0 : 1;
    }

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
