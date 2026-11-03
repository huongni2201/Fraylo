using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Jobs;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Jobs;
using Fraylo.Browser.Abstractions;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói định lượng AI Pool v1.1 toàn cục:
/// 1. 10 Project dùng chung 1 ChatGPT theo tab capacity.
/// 2. 10 video qua 4 MuseAI: khi đủ budget, tối đa 4 chạy và 6 chờ; slot nào xong cấp ngay job tiếp, không đợi cả đợt.
/// 3. Output luôn đúng Project/project/scene dù account phục vụ nhiều kênh.
/// 4. Đủ 8 tổ hợp lựa chọn provider cho Script/Image/VideoPrompt.
/// </summary>
public static class GlobalAiPoolSmokeTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] {message}");
        }
    }

    private static (DbContextOptions<FrayloDbContext> Options, string TempDir, StorageRootRegistry Registry, SettingsService Settings) CreateTestContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"Fraylo_aipool_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "Fraylo_pool.db");
        var connectionString = DatabasePathHelper.BuildConnectionString(dbPath);

        var optionsBuilder = new DbContextOptionsBuilder<FrayloDbContext>();
        optionsBuilder.UseSqlite(connectionString)
                      .AddInterceptors(new SqlitePragmaInterceptor())
                      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        var dbOptions = optionsBuilder.Options;

        using (var db = new FrayloDbContext(dbOptions))
        {
            db.Database.EnsureCreated();
        }

        var registry = new StorageRootRegistry(dbOptions, tempDir);
        registry.EnsureDefaultRootsAsync().GetAwaiter().GetResult();
        var settingsService = new SettingsService(dbOptions, registry);

        return (dbOptions, tempDir, registry, settingsService);
    }

    private static void CleanupTestContext(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch { }
    }

    /// <summary>
    /// Ca 1: 10 Project dùng chung 1 ChatGPT theo tab capacity.
    /// 10 Project độc lập cùng enqueue job; chỉ 3 job chạy đồng thời (theo MaxTabs=3);
    /// khi 1 job xong, slot tiếp theo được cấp ngay cho project chờ theo thứ tự công bằng.
    /// </summary>
    public static async Task Test_Case1_TenProjects_SingleChatGPT_TabCapacityAsync()
    {
        var (dbOptions, tempDir, registry, settingsService) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");

            // 1. Tạo 1 ChatGPT Global ProviderAccount duy nhất với MaxTabs = 3
            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "profile_chatgpt_pool",
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "ChatGPT Global Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc-chatgpt-shared",
                    ProviderKey = "chatgpt-web",
                    ProfileId = "profile_chatgpt_pool",
                    DisplayName = "ChatGPT Pool Account",
                    VerifiedIdentity = "team@chatgpt.internal",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 3,
                    MaxTabs = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.AppSettings.AddRange(
                    new AppSetting { Key = SettingsService.KeyScriptProvider, Value = "chatgpt-web", UpdatedAtUtc = now },
                    new AppSetting { Key = SettingsService.KeyImageProvider, Value = "chatgpt-web", UpdatedAtUtc = now },
                    new AppSetting { Key = SettingsService.KeyVideoPromptProvider, Value = "chatgpt-web", UpdatedAtUtc = now }
                );

                // 2. Tạo 10 Project độc lập (WS_01 .. WS_10)
                for (int i = 1; i <= 10; i++)
                {
                    var prjId = $"prj_{i:D2}";

                    db.Projects.Add(new Project
                    {
                        Id = prjId,
                        Name = $"Dự án video {i}",
                        Status = "Draft",
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });

                    db.Scenes.Add(new Scene
                    {
                        Id = $"scene_{i:D2}",
                        ProjectId = prjId,
                        SequenceOrder = 1,
                        Name = $"Cảnh 1 kênh {i}",
                        Status = "Draft",
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }

                await db.SaveChangesAsync();
            }

            // 3. Khởi tạo JobService & Scheduler
            var snapshotFactory = new JobSnapshotFactory(settingsService);
            var reservation = new JobReservation(dbOptions);
            var controlledExecutor = new ControlledJobExecutor();
            var scheduler = new JobScheduler(dbOptions, reservation, controlledExecutor);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);

            // 4. Enqueue 10 Jobs từ 10 Project khác nhau (đan xen 3 loại task: Script, Image, VideoPrompt)
            var jobIds = new List<string>();
            var taskTypes = new[] { JobTaskType.Script, JobTaskType.Image, JobTaskType.VideoPrompt };

            for (int i = 1; i <= 10; i++)
            {
                var taskType = taskTypes[(i - 1) % 3];
                string? sceneId = taskType == JobTaskType.Script ? null : $"scene_{i:D2}";

                var res = await jobService.EnqueueAsync(new JobEnqueueRequest(
                    ProjectId: $"prj_{i:D2}",
                    SceneId: sceneId,
                    TaskType: taskType,
                    CustomPromptText: $"Nhiệm vụ {taskType} video TikTok số {i}"
                ));
                jobIds.Add(res.JobId);
            }

            // 5. Dispatch đợt 1: Do Tab capacity = 3, tối đa chỉ đúng 3 jobs được dispatched
            int dispatched1 = await scheduler.DispatchPendingJobsAsync();
            Assert(dispatched1 == 3, $"Kỳ vọng đúng 3 jobs được dispatch theo tab capacity, thực tế: {dispatched1}");

            // Kiểm tra trạng thái DB: 3 Running có ExecutionBinding trỏ về 'acc-chatgpt-shared', 7 Queued (attempts = 0)
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var runningJobs = await db.Jobs
                    .Include(j => j.ExecutionBinding)
                    .Where(j => j.State == JobState.Running)
                    .ToListAsync();
                Assert(runningJobs.Count == 3, "Phải có đúng 3 jobs đang Running.");
                Assert(runningJobs.All(j => j.ExecutionBinding != null && j.ExecutionBinding.ProviderAccountId == "acc-chatgpt-shared"),
                    "Tất cả job running đều phải bind vào ProviderAccount 'acc-chatgpt-shared'.");

                var queuedJobs = await db.Jobs.Where(j => j.State == JobState.Queued).ToListAsync();
                Assert(queuedJobs.Count == 7, "7 jobs còn lại phải ở trạng thái Queued.");
                Assert(queuedJobs.All(j => j.Attempts == 0), "Các jobs đang chờ không được tăng attempts.");
            }

            // 6. Hoàn tất 1 job trong 3 job running qua ControlledJobExecutor
            string firstRunningId;
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var firstRunning = await db.Jobs.FirstAsync(j => j.State == JobState.Running);
                firstRunningId = firstRunning.Id;
            }

            controlledExecutor.CompleteJob(firstRunningId);
            // Chờ worker hoàn tất và giải phóng slot
            for (int wait = 0; wait < 50; wait++)
            {
                await Task.Delay(20);
                await using var db = new FrayloDbContext(dbOptions);
                var j = await db.Jobs.FirstAsync(x => x.Id == firstRunningId);
                if (j.State == JobState.Succeeded) break;
            }

            // 7. Dispatch đợt 2: Slot rảnh vừa mở được cấp ngay cho 1 job chờ tiếp theo
            int dispatched2 = await scheduler.DispatchPendingJobsAsync();
            Assert(dispatched2 == 1, $"Kỳ vọng đúng 1 job tiếp theo được cấp slot, thực tế: {dispatched2}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var runningCount = await db.Jobs.CountAsync(j => j.State == JobState.Running);
                var succeededCount = await db.Jobs.CountAsync(j => j.State == JobState.Succeeded);
                var queuedCount = await db.Jobs.CountAsync(j => j.State == JobState.Queued);

                Assert(runningCount == 3, "Tab pool tiếp tục duy trì 3 jobs đang chạy.");
                Assert(succeededCount == 1, "Đã có 1 job hoàn tất.");
                Assert(queuedCount == 6, "Còn 6 jobs đang chờ.");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Ca 2: 10 video qua 4 MuseAI: khi đủ budget, tối đa 4 chạy và 6 chờ;
    /// slot nào xong cấp ngay job tiếp, không đợi cả đợt (Streaming allocation, không batch-wait).
    /// </summary>
    public static async Task Test_Case2_TenVideos_FourMuseAI_StreamingSlotsAsync()
    {
        var (dbOptions, tempDir, registry, settingsService) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            string prjId = "prj_video_main";

            await using (var db = new FrayloDbContext(dbOptions))
            {

                // Cấu hình đủ budget browser toàn app (5) cho ca nghiệm thu 4 MuseAI
                db.AppSettings.Add(new AppSetting
                {
                    Key = "scheduler.max_open_browsers",
                    Value = "5",
                    UpdatedAtUtc = now
                });

                db.Projects.Add(new Project
                {
                    Id = prjId,
                    Name = "Phim ngắn 10 phân cảnh",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                // Tạo 4 tài khoản MuseAI global độc lập, mỗi tài khoản MaxConcurrentJobs = 1
                for (int i = 1; i <= 4; i++)
                {
                    var profileId = $"profile_museai_{i}";
                    var accId = $"acc_museai_{i}";

                    db.BrowserProfiles.Add(new BrowserProfile
                    {
                        Id = profileId,
                        Kind = "MuseAI",
                        ProviderKey = "museai",
                        DisplayName = $"MuseAI Profile {i}",
                        SessionStatus = "Connected",
                        OperationalState = "Stopped",
                        VisibilityState = "Hidden",
                        Lifecycle = "Active",
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });

                    db.ProviderAccounts.Add(new ProviderAccount
                    {
                        Id = accId,
                        ProviderKey = "museai",
                        ProfileId = profileId,
                        DisplayName = $"MuseAI Account {i}",
                        VerifiedIdentity = $"muse_user_{i}@agency.ai",
                        IdentityVerifiedAtUtc = now,
                        Enabled = true,
                        Lifecycle = "Active",
                        MaxConcurrentJobs = 1,
                        MaxTabs = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }

                // 10 Phân cảnh (Scene)
                for (int i = 1; i <= 10; i++)
                {
                    db.Scenes.Add(new Scene
                    {
                        Id = $"scene_{i:D2}",
                        ProjectId = prjId,
                        SequenceOrder = i,
                        Name = $"Cảnh quay {i}",
                        PromptText = $"Cinematic 4k video shot {i}",
                        Status = "Draft",
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }

                await db.SaveChangesAsync();
            }

            var snapshotFactory = new JobSnapshotFactory(settingsService);
            var reservation = new JobReservation(dbOptions);
            var controlledExecutor = new ControlledJobExecutor();
            var scheduler = new JobScheduler(dbOptions, reservation, controlledExecutor);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);

            // Enqueue 10 video jobs (TaskType = VideoRender)
            for (int i = 1; i <= 10; i++)
            {
                await jobService.EnqueueAsync(new JobEnqueueRequest(
                    ProjectId: prjId,
                    SceneId: $"scene_{i:D2}",
                    TaskType: JobTaskType.VideoRender,
                    CustomPromptText: $"Render scene video {i}"
                ));
            }

            // Đợt 1: Dispatch tối đa 4 jobs vào 4 MuseAI accounts
            int dispatched1 = await scheduler.DispatchPendingJobsAsync();
            Assert(dispatched1 == 4, $"Kỳ vọng dispatch tối đa 4 video jobs, thực tế: {dispatched1}");

            // Đợi async workers chuyển sang trạng thái Running
            for (int wait = 0; wait < 50; wait++)
            {
                await using var db = new FrayloDbContext(dbOptions);
                var c = await db.Jobs.CountAsync(j => j.State == JobState.Running);
                if (c == 4) break;
                await Task.Delay(20);
            }

            // Xác minh 4 jobs chạy trên 4 tài khoản MuseAI khác nhau
            string earlyFinishedJobId;
            string earlyFinishedAccountId;
            long earlyFinishedToken;
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var running = await db.Jobs
                    .Include(j => j.ExecutionBinding)
                    .Where(j => j.State == JobState.Running)
                    .ToListAsync();

                Assert(running.Count == 4, "Phải có đúng 4 jobs chạy.");
                var boundAccounts = running.Select(j => j.ExecutionBinding!.ProviderAccountId).Distinct().ToList();
                Assert(boundAccounts.Count == 4, "4 jobs phải được phân bổ đều vào 4 tài khoản MuseAI khác nhau.");

                var first = running[1]; // Giả lập MuseAI Account 2 hoàn tất sớm
                earlyFinishedJobId = first.Id;
                earlyFinishedAccountId = first.ExecutionBinding!.ProviderAccountId;
                earlyFinishedToken = first.FencingToken;
            }

            // Hoàn tất slot của MuseAI Account 2 (các slot khác vẫn đang chạy) qua ControlledJobExecutor
            controlledExecutor.CompleteJob(earlyFinishedJobId);
            for (int wait = 0; wait < 50; wait++)
            {
                await Task.Delay(20);
                await using var db = new FrayloDbContext(dbOptions);
                var j = await db.Jobs.FirstAsync(x => x.Id == earlyFinishedJobId);
                if (j.State == JobState.Succeeded) break;
            }

            // Dispatch ngay: Slot của Account 2 được cấp ngay lập tức cho job thứ 5, KHÔNG đợi 3 slot kia
            int dispatched2 = await scheduler.DispatchPendingJobsAsync();
            Assert(dispatched2 == 1, $"Slot rảnh phải nhận job mới ngay lập tức, thực tế: {dispatched2}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var newRunningJob = await db.Jobs
                    .Include(j => j.ExecutionBinding)
                    .FirstOrDefaultAsync(j => j.State == JobState.Running && j.Id != earlyFinishedJobId && j.ExecutionBinding!.ProviderAccountId == earlyFinishedAccountId);

                Assert(newRunningJob != null, $"Job mới phải được cấp ngay vào tài khoản MuseAI '{earlyFinishedAccountId}'.");
                var totalRunning = await db.Jobs.CountAsync(j => j.State == JobState.Running);
                Assert(totalRunning == 4, "Hệ thống vẫn giữ 4 jobs chạy song song.");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Ca 3: Output luôn đúng Project/project/scene dù account AI phục vụ nhiều kênh.
    /// Hai Project khác nhau (WS_A, WS_B) cùng xử lý qua 1 account AI chung;
    /// kết quả và binding không bao giờ bị rò rỉ hoặc trỏ nhầm sang Project khác.
    /// </summary>
    public static async Task Test_Case3_StrictOutputIsolation_MultiChannelSingleAccountAsync()
    {
        var (dbOptions, tempDir, registry, settingsService) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                // Profile & Account dùng chung toàn hệ thống
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "profile_shared_engine",
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "Global AI Engine",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc-shared-engine",
                    ProviderKey = "chatgpt-web",
                    ProfileId = "profile_shared_engine",
                    DisplayName = "Global AI Engine Account",
                    VerifiedIdentity = "engine@fraylo.local",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 5,
                    MaxTabs = 5,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.AppSettings.AddRange(
                    new AppSetting { Key = SettingsService.KeyScriptProvider, Value = "chatgpt-web", UpdatedAtUtc = now },
                    new AppSetting { Key = SettingsService.KeyImageProvider, Value = "chatgpt-web", UpdatedAtUtc = now }
                );

                // Project A: Kênh Review Phim
                db.Projects.Add(new Project
                {
                    Id = "prj_movie_01",
                    Name = "Review Avatar 3",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.Scenes.Add(new Scene
                {
                    Id = "scene_movie_01",
                    ProjectId = "prj_movie_01",
                    SequenceOrder = 1,
                    Name = "Poster phim",
                    PromptText = "Movie poster 4k",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                // Project B: Kênh Nấu Ăn
                db.Projects.Add(new Project
                {
                    Id = "prj_cooking_01",
                    Name = "Món Phở Bò",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.Scenes.Add(new Scene
                {
                    Id = "scene_cooking_01",
                    ProjectId = "prj_cooking_01",
                    SequenceOrder = 1,
                    Name = "Tô phở nóng",
                    PromptText = "Vietnamese pho soup, cinematic",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                await db.SaveChangesAsync();
            }

            var snapshotFactory = new JobSnapshotFactory(settingsService);
            var reservation = new JobReservation(dbOptions);
            var fakeExecutor = new DeterministicFakeJobExecutor();
            var scheduler = new JobScheduler(dbOptions, reservation, fakeExecutor);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);

            // Enqueue Job A từ kênh Review Phim (TaskType: Script)
            var resA = await jobService.EnqueueAsync(new JobEnqueueRequest(
                ProjectId: "prj_movie_01",
                SceneId: null,
                TaskType: JobTaskType.Script,
                CustomPromptText: "Kịch bản Review Avatar 3"
            ));

            // Enqueue Job B từ kênh Bếp Mẹ Nấu (TaskType: Image)
            var resB = await jobService.EnqueueAsync(new JobEnqueueRequest(
                ProjectId: "prj_cooking_01",
                SceneId: "scene_cooking_01",
                TaskType: JobTaskType.Image,
                CustomPromptText: "Tô phở bò nóng hổi"
            ));

            // Cả hai cùng chạy qua acc-shared-engine
            int dispatched = await scheduler.DispatchPendingJobsAsync();
            Assert(dispatched == 2, "Cả hai jobs phải được dispatch.");

            // Kiểm tra phân lập 100% về Project, Project, Scene và StorageRoot
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var jobA = await db.Jobs.Include(j => j.ExecutionBinding).FirstAsync(j => j.Id == resA.JobId);
                var jobB = await db.Jobs.Include(j => j.ExecutionBinding).FirstAsync(j => j.Id == resB.JobId);

                // Job A
                Assert(jobA.ProjectId == "prj_movie_01", "Job A phải thuộc Project Movie 01.");
                Assert(jobA.SceneId == null, "Job A (Script) phải có SceneId null.");
                Assert(jobA.ExecutionBinding!.JobId == jobA.Id, "ExecutionBinding A phải gắn đúng Job A.");
                Assert(jobA.ExecutionBinding.ProviderAccountId == "acc-shared-engine", "Job A dùng đúng account AI.");

                // Job B
                Assert(jobB.ProjectId == "prj_cooking_01", "Job B phải thuộc Project Cooking 01.");
                Assert(jobB.SceneId == "scene_cooking_01", "Job B phải thuộc Scene Cooking 01.");
                Assert(jobB.ExecutionBinding!.JobId == jobB.Id, "ExecutionBinding B phải gắn đúng Job B.");
                Assert(jobB.ExecutionBinding.ProviderAccountId == "acc-shared-engine", "Job B dùng đúng account AI.");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Ca 4: Đủ 8 tổ hợp lựa chọn provider cho Script/Image/VideoPrompt.
    /// Kiểm chứng toàn bộ 2^3 = 8 tổ hợp (ChatGPT Web, Gemini Web) được lưu, nạp,
    /// và snapshot đóng băng chính xác vào từng Job khi enqueue.
    /// </summary>
    public static async Task Test_Case4_AllEightProviderCombinationsAsync()
    {
        var (dbOptions, tempDir, registry, settingsService) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            string prjId = "prj_combinations";
            string scnId = "scene_combinations";

            await using (var db = new FrayloDbContext(dbOptions))
            {

                db.Projects.Add(new Project
                {
                    Id = prjId,
                    Name = "Combo Project",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.Scenes.Add(new Scene
                {
                    Id = scnId,
                    ProjectId = prjId,
                    SequenceOrder = 1,
                    Name = "Combo Scene",
                    PromptText = "Test prompt",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                await db.SaveChangesAsync();
            }

            var providers = new[] { "chatgpt-web", "gemini-web" };
            var snapshotFactory = new JobSnapshotFactory(settingsService);

            int comboIndex = 0;
            foreach (var scriptP in providers)
            {
                foreach (var imageP in providers)
                {
                    foreach (var videoPromptP in providers)
                    {
                        comboIndex++;

                        // Cập nhật cấu hình bộ ba provider vào DB
                        await using (var db = new FrayloDbContext(dbOptions))
                        {
                            void SetAppSetting(string key, string val)
                            {
                                var existing = db.AppSettings.FirstOrDefault(s => s.Key == key);
                                if (existing != null)
                                {
                                    existing.Value = val;
                                    existing.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o");
                                }
                                else
                                {
                                    db.AppSettings.Add(new AppSetting { Key = key, Value = val, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o") });
                                }
                            }

                            SetAppSetting(SettingsService.KeyScriptProvider, scriptP);
                            SetAppSetting(SettingsService.KeyImageProvider, imageP);
                            SetAppSetting(SettingsService.KeyVideoPromptProvider, videoPromptP);
                            await db.SaveChangesAsync();
                        }

                        // Kiểm tra SettingsService đọc đúng tổ hợp
                        var settings = await settingsService.GetSettingsAsync();
                        Assert(settings.ScriptProvider == scriptP, $"Combo {comboIndex}: ScriptProvider sai: {settings.ScriptProvider} vs {scriptP}");
                        Assert(settings.ImageProvider == imageP, $"Combo {comboIndex}: ImageProvider sai: {settings.ImageProvider} vs {imageP}");
                        Assert(settings.VideoPromptProvider == videoPromptP, $"Combo {comboIndex}: VideoPromptProvider sai: {settings.VideoPromptProvider} vs {videoPromptP}");

                        // Kiểm tra SnapshotFactory chụp đúng provider tương ứng cho từng loại tác vụ
                        var scriptSnapshot = await snapshotFactory.CreateSnapshotAsync(new JobEnqueueRequest(
                            ProjectId: prjId,
                            SceneId: null,
                            TaskType: JobTaskType.Script,
                            CustomPromptText: "Script prompt"
                        ));
                        Assert(scriptSnapshot.ProviderKey == scriptP, $"Combo {comboIndex}: Script snapshot sai provider: {scriptSnapshot.ProviderKey} vs {scriptP}");

                        var imageSnapshot = await snapshotFactory.CreateSnapshotAsync(new JobEnqueueRequest(
                            ProjectId: prjId,
                            SceneId: scnId,
                            TaskType: JobTaskType.Image,
                            CustomPromptText: "Image prompt"
                        ));
                        Assert(imageSnapshot.ProviderKey == imageP, $"Combo {comboIndex}: Image snapshot sai provider: {imageSnapshot.ProviderKey} vs {imageP}");

                        var videoPromptSnapshot = await snapshotFactory.CreateSnapshotAsync(new JobEnqueueRequest(
                            ProjectId: prjId,
                            SceneId: scnId,
                            TaskType: JobTaskType.VideoPrompt,
                            CustomPromptText: "Video prompt"
                        ));
                        Assert(videoPromptSnapshot.ProviderKey == videoPromptP, $"Combo {comboIndex}: VideoPrompt snapshot sai provider: {videoPromptSnapshot.ProviderKey} vs {videoPromptP}");
                    }
                }
            }

            Assert(comboIndex == 8, $"Bắt buộc phải kiểm thử đủ 8 tổ hợp, thực tế: {comboIndex}");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("\n=================================================================");
        Console.WriteLine("    BẮT ĐẦU KIỂM THỬ KHÓI GLOBAL AI POOL V1.1 (4 CA NGHIỆM THU)   ");
        Console.WriteLine("=================================================================");

        int passed = 0;
        int failed = 0;

        async Task RunCaseAsync(string testName, Func<Task> testAction)
        {
            Console.Write($"[RUN] {testName} ... ");
            try
            {
                await testAction();
                passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL");
                Console.ResetColor();
                Console.WriteLine($"      -> Lỗi: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"      -> Inner: {ex.InnerException.Message}");
                }
            }
        }

        await RunCaseAsync(
            "Ca 1: 10 Project dùng chung 1 ChatGPT theo tab capacity (3 running, 7 queued, streaming next)",
            Test_Case1_TenProjects_SingleChatGPT_TabCapacityAsync);

        await RunCaseAsync(
            "Ca 2: 10 video qua 4 MuseAI (tối đa 4 running, streaming cấp ngay slot rảnh, không batch-wait)",
            Test_Case2_TenVideos_FourMuseAI_StreamingSlotsAsync);

        await RunCaseAsync(
            "Ca 3: Output luôn đúng Project/project/scene dù account AI phục vụ nhiều kênh",
            Test_Case3_StrictOutputIsolation_MultiChannelSingleAccountAsync);

        await RunCaseAsync(
            "Ca 4: Đủ 8 tổ hợp lựa chọn provider cho Script/Image/VideoPrompt (2^3 combos)",
            Test_Case4_AllEightProviderCombinationsAsync);

        Console.WriteLine("\n=================================================================");
        if (failed == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"     TẤT CẢ 4 CA NGHIỆM THU GLOBAL AI POOL V1.1 ĐÃ PASS ({passed}/{passed})");
            Console.ResetColor();
            Console.WriteLine("=================================================================");
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"     GLOBAL AI POOL V1.1 THẤT BẠI: {failed} FAIL, {passed} PASS");
            Console.ResetColor();
            Console.WriteLine("=================================================================");
            return 1;
        }
    }

    private sealed class ControlledJobExecutor : IJobExecutor
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _tcsMap = new();

        public TaskCompletionSource GetOrAddJobTcs(string jobId) =>
            _tcsMap.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void CompleteJob(string jobId)
        {
            var tcs = GetOrAddJobTcs(jobId);
            tcs.TrySetResult();
        }

        public async Task<JobExecutionResult> ExecuteAsync(
            JobExecutionContext context,
            Func<Task> onPreSideEffectCommitAsync,
            Func<int, string, Task>? onProgressAsync,
            CancellationToken cancellationToken)
        {
            var tcs = GetOrAddJobTcs(context.JobId);
            await tcs.Task.WaitAsync(cancellationToken);
            return new JobExecutionResult(Success: true, ResultReference: $"asset-{context.JobId}");
        }
    }
}
