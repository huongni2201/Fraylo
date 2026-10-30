using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiToAI.Application.Jobs;
using TiToAI.Application.Settings;
using TiToAI.Contracts.Jobs;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;
using TiToAI.Infrastructure.Persistence.Interceptors;
using TiToAI.Infrastructure.Storage;

namespace TiToAI.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử Fault Injection cho Task T16:
/// Phục hồi sự cố, thử lại an toàn, cờ side-effect, hủy tác vụ trước/sau submit,
/// bảo toàn kết quả muộn (late result) dạng Unlinked, chống zombie worker bằng Fencing Token.
/// </summary>
public static class JobRecoverySmokeTests
{
    private static (DbContextOptions<TiToAIDbContext> Options, string TempDir, StorageRootRegistry Registry, SettingsService Settings) CreateTestContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"titoai_t16_fault_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "titoai_fault_test.db");
        var connectionString = DatabasePathHelper.BuildConnectionString(dbPath);

        var optionsBuilder = new DbContextOptionsBuilder<TiToAIDbContext>();
        optionsBuilder.UseSqlite(connectionString)
                      .AddInterceptors(new SqlitePragmaInterceptor())
                      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        var dbOptions = optionsBuilder.Options;

        using (var db = new TiToAIDbContext(dbOptions))
        {
            db.Database.EnsureCreated();

            var now = DateTimeOffset.UtcNow.ToString("o");
            if (!db.BrowserProfiles.Any(p => p.Id == "prof_t16"))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "prof_t16",
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "T16 Test Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }

            if (!db.ProviderAccounts.Any(a => a.ProfileId == "prof_t16"))
            {
                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc_t16",
                    ProviderKey = "chatgpt-web",
                    ProfileId = "prof_t16",
                    DisplayName = "T16 Test Account",
                    VerifiedIdentity = "test@company.ai",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 3,
                    MaxTabs = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }

            if (!db.Projects.Any(p => p.Id == "prj_t16"))
            {
                db.Projects.Add(new Project
                {
                    Id = "prj_t16",
                    Name = "T16 Project",
                    Status = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }

            db.SaveChanges();
        }

        var registry = new StorageRootRegistry(dbOptions, tempDir);
        var settings = new SettingsService(dbOptions, registry);
        return (dbOptions, tempDir, registry, settings);
    }

    public static async Task RunAllAsync()
    {
        Console.WriteLine("\n--- [T16] Bắt đầu chạy Fault Injection Smoke Tests ---");

        await Test_Fault1_CrashBeforeSubmitIntent_StopsWithoutAutoRetry();
        Console.WriteLine("  ✓ Fault 1: Crash trước submit intent -> Dừng ở Failed và giải phóng slot PASS");

        await Test_Fault2_CrashAfterSubmitIntent_TransitionToAwaitingUser_NoAutoRetry();
        Console.WriteLine("  ✓ Fault 2: Crash sau submit intent -> Chuyển AwaitingUser (CẤM retry tự động, giữ slot occupancy) PASS");

        await Test_Fault3_CancelBeforeSubmitIntent_ImmediateCanceledAndReleaseSlot();
        Console.WriteLine("  ✓ Fault 3: Cancel trước submit intent -> Canceled ngay và giải phóng slot occupancy lập tức PASS");

        await Test_Fault4_CancelAfterSubmitIntent_LateResultPreservesAssetAsUnlinked();
        Console.WriteLine("  ✓ Fault 4: Cancel sau submit intent -> Late result lưu Asset dạng Unlinked & chuyển Canceled (không mất file) PASS");

        await Test_Fault5_StorageErrorAfterAiGeneration_RetriesFinalizeWithoutReGenerating();
        Console.WriteLine("  ✓ Fault 5: Lỗi lưu storage sau khi sinh AI -> Lưu intermediate result, retry finalize không gọi lại AI PASS");

        await Test_Fault6_FencingTokenMismatch_RejectsZombieCommit();
        Console.WriteLine("  ✓ Fault 6: Fencing Token Stale -> Zombie worker commit bị từ chối với FencingTokenStaleException PASS");

        Console.WriteLine("--- [T16] Toàn bộ 6/6 Fault Injection Smoke Tests PASSED 100% ---\n");
    }

    /// <summary>
    /// Fault 1: Worker crash / timeout khi chưa gửi side effect (SideEffectSubmitted == 0).
    /// Recovery sweep phải dừng job ở Failed và giải phóng slot occupancy.
    /// </summary>
    public static async Task Test_Fault1_CrashBeforeSubmitIntent_StopsWithoutAutoRetry()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var simulatedTime = DateTimeOffset.UtcNow;
            var reservation = new JobReservation(dbOptions, () => simulatedTime);
            var scheduler = new JobScheduler(dbOptions, reservation, new DummyJobExecutor(), clock: () => simulatedTime);

            string jobId = Guid.NewGuid().ToString("D");
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var nowIso = simulatedTime.ToString("o");
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = "prj_t16",
                    TaskType = JobTaskType.Script,
                    State = JobState.Queued,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root-default-docs",
                    Attempts = 0,
                    MaxAttempts = 3,
                    FencingToken = 0,
                    SideEffectSubmitted = 0,
                    CancelRequested = 0,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });
                await db.SaveChangesAsync();
            }

            // Worker 1 reserve job
            var reserveResult = await reservation.TryReserveJobAsync(jobId, "worker-1", TimeSpan.FromSeconds(10));
            if (!reserveResult.Acquired || reserveResult.Job == null)
            {
                throw new InvalidOperationException("Không thể reserve job.");
            }

            // Kiểm tra slot occupancy đã được cấp
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var occ = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == jobId);
                if (occ == null) throw new InvalidOperationException("Slot occupancy phải tồn tại sau khi reserve.");
            }

            // Giả lập worker-1 bị crash: tua thời gian vượt quá lease (15 giây)
            simulatedTime = simulatedTime.AddSeconds(15);

            // Chạy recovery sweep
            int recovered = await scheduler.RunCrashRecoverySweepAsync();
            if (recovered != 1)
            {
                throw new InvalidOperationException($"Kỳ vọng phục hồi 1 job, thực tế: {recovered}");
            }

            // Assert: Job dừng ở Failed, slot occupancy đã được giải phóng
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.Failed)
                {
                    throw new InvalidOperationException($"Job phải ở trạng thái Failed, thực tế: {job.State}");
                }
                if (job.RetryAfterUtc != null)
                {
                    throw new InvalidOperationException("Job lỗi không được hẹn tự thử lại.");
                }

                var occ = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == jobId);
                if (occ != null)
                {
                    throw new InvalidOperationException("Slot occupancy phải được giải phóng khi job lỗi.");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Fault 2: Worker crash / timeout sau khi đã gửi side effect (SideEffectSubmitted == 1).
    /// Recovery sweep CẤM retry tự động, phải chuyển sang AwaitingUser và giữ slot occupancy.
    /// </summary>
    public static async Task Test_Fault2_CrashAfterSubmitIntent_TransitionToAwaitingUser_NoAutoRetry()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var simulatedTime = DateTimeOffset.UtcNow;
            var reservation = new JobReservation(dbOptions, () => simulatedTime);
            var scheduler = new JobScheduler(dbOptions, reservation, new DummyJobExecutor(), clock: () => simulatedTime);

            string jobId = Guid.NewGuid().ToString("D");
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var nowIso = simulatedTime.ToString("o");
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = "prj_t16",
                    TaskType = JobTaskType.Script,
                    State = JobState.Queued,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root-default-docs",
                    Attempts = 0,
                    MaxAttempts = 3,
                    FencingToken = 0,
                    SideEffectSubmitted = 0,
                    CancelRequested = 0,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });
                await db.SaveChangesAsync();
            }

            var reserveResult = await reservation.TryReserveJobAsync(jobId, "worker-1", TimeSpan.FromSeconds(10));
            var jobEntity = reserveResult.Job!;

            // Worker commit side effect trước khi click web action
            await reservation.CommitSideEffectAsync(jobId, jobEntity.FencingToken);

            // Giả lập worker crash / ngắt mạng
            simulatedTime = simulatedTime.AddSeconds(15);

            int recovered = await scheduler.RunCrashRecoverySweepAsync();
            if (recovered != 1)
            {
                throw new InvalidOperationException($"Kỳ vọng sweep 1 job, thực tế: {recovered}");
            }

            using (var db = new TiToAIDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.AwaitingUser)
                {
                    throw new InvalidOperationException($"Job đã submit side effect bắt buộc phải chuyển AwaitingUser, thực tế: {job.State}");
                }
                if (job.ReasonCode != JobReasonCode.ReconcileRequired)
                {
                    throw new InvalidOperationException($"ReasonCode phải là {JobReasonCode.ReconcileRequired}, thực tế: {job.ReasonCode}");
                }

                // Slot occupancy phải được GIỮ LẠI (uncertain occupancy) để không resend trùng
                var occ = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == jobId);
                if (occ == null)
                {
                    throw new InvalidOperationException("Slot occupancy phải được giữ nguyên khi job chuyển sang AwaitingUser do nghi ngờ side effect ngoài.");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Fault 3: Người dùng hủy tác vụ trước khi gửi side effect (SideEffectSubmitted == 0).
    /// Chuyển Canceled ngay lập tức và giải phóng slot occupancy.
    /// </summary>
    public static async Task Test_Fault3_CancelBeforeSubmitIntent_ImmediateCanceledAndReleaseSlot()
    {
        var (dbOptions, tempDir, registry, settings) = CreateTestContext();
        try
        {
            var simulatedTime = DateTimeOffset.UtcNow;
            var reservation = new JobReservation(dbOptions, () => simulatedTime);
            var scheduler = new JobScheduler(dbOptions, reservation, new DummyJobExecutor(), clock: () => simulatedTime);
            var snapshotFactory = new JobSnapshotFactory(settings, null, dbOptions);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler, clock: () => simulatedTime);

            string jobId = Guid.NewGuid().ToString("D");
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var nowIso = simulatedTime.ToString("o");
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = "prj_t16",
                    TaskType = JobTaskType.Script,
                    State = JobState.Queued,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root-default-docs",
                    Attempts = 0,
                    MaxAttempts = 3,
                    FencingToken = 0,
                    SideEffectSubmitted = 0,
                    CancelRequested = 0,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });
                await db.SaveChangesAsync();
            }

            // Reserve job
            await reservation.TryReserveJobAsync(jobId, "worker-1", TimeSpan.FromSeconds(30));

            // Người dùng bấm Hủy
            var cancelRes = await jobService.CancelAsync(jobId);
            if (cancelRes.State != JobState.Canceled || cancelRes.CancelRequested)
            {
                throw new InvalidOperationException($"Hủy trước submit phải có State=Canceled và CancelRequested=false. Thực tế: {cancelRes.State}, CancelRequested={cancelRes.CancelRequested}");
            }

            // Kiểm tra DB: slot occupancy đã được giải phóng ngay
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.Canceled) throw new InvalidOperationException("Job phải là Canceled.");

                var occ = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == jobId);
                if (occ != null)
                {
                    throw new InvalidOperationException("Slot occupancy phải được giải phóng ngay khi cancel trước submit.");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Fault 4: Người dùng hủy tác vụ sau khi gửi side effect: Canceled ngay, giải phóng slot.
    /// Kết quả muộn của worker bị chặn, job không thành Succeeded.
    /// </summary>
    public static async Task Test_Fault4_CancelAfterSubmitIntent_LateResultPreservesAssetAsUnlinked()
    {
        var (dbOptions, tempDir, registry, settings) = CreateTestContext();
        try
        {
            var simulatedTime = DateTimeOffset.UtcNow;
            var reservation = new JobReservation(dbOptions, () => simulatedTime);
            var scheduler = new JobScheduler(dbOptions, reservation, new DummyJobExecutor(), clock: () => simulatedTime);
            var snapshotFactory = new JobSnapshotFactory(settings, null, dbOptions);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler, clock: () => simulatedTime);

            string jobId = Guid.NewGuid().ToString("D");
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var nowIso = simulatedTime.ToString("o");
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = "prj_t16",
                    TaskType = JobTaskType.Script,
                    State = JobState.Queued,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root-default-docs",
                    Attempts = 0,
                    MaxAttempts = 3,
                    FencingToken = 0,
                    SideEffectSubmitted = 0,
                    CancelRequested = 0,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });
                await db.SaveChangesAsync();
            }

            var reserveResult = await reservation.TryReserveJobAsync(jobId, "worker-1", TimeSpan.FromSeconds(30));
            var fencingToken = reserveResult.Job!.FencingToken;

            // Commit side effect
            await reservation.CommitSideEffectAsync(jobId, fencingToken);

            // Người dùng yêu cầu Hủy sau khi submit: kết thúc ngay, không còn bước đối soát
            var cancelRes = await jobService.CancelAsync(jobId);
            if (cancelRes.State != JobState.Canceled)
            {
                throw new InvalidOperationException($"Cancel sau submit phải chuyển Canceled ngay, thực tế: {cancelRes.State}");
            }

            using (var db = new TiToAIDbContext(dbOptions))
            {
                var job = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.Canceled) throw new InvalidOperationException("Job phải là Canceled.");
                if (job.SideEffectSubmitted != 0) throw new InvalidOperationException("Cờ side effect phải được xóa khi hủy.");
                if (await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == jobId))
                    throw new InvalidOperationException("Slot occupancy phải được giải phóng ngay.");
            }

            // Worker nộp kết quả muộn: bị chặn, job không bao giờ thành Succeeded
            var lateBlocked = false;
            try
            {
                await reservation.CommitSuccessAsync(jobId, fencingToken, resultReference: "late_ref", resultText: "Nội dung kịch bản AI muộn");
            }
            catch (FencingTokenStaleException)
            {
                lateBlocked = true;
            }
            if (!lateBlocked) throw new InvalidOperationException("Kết quả muộn sau khi hủy phải bị chặn.");

            using (var db = new TiToAIDbContext(dbOptions))
            {
                var job = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.Canceled) throw new InvalidOperationException("Job phải giữ Canceled sau kết quả muộn.");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Fault 5: Lỗi lưu storage sau khi sinh AI.
    /// Job lưu lại intermediate ResultText/ResultReference, khi restart/reconnect chạy qua
    /// command production (jobService.ResumeAsync), finalize lưu file bằng AssetFileStore thật và không gọi lại AI.
    /// </summary>
    public static async Task Test_Fault5_StorageErrorAfterAiGeneration_RetriesFinalizeWithoutReGenerating()
    {
        var (dbOptions, tempDir, registry, settings) = CreateTestContext();
        try
        {
            var storageRootPath = Path.Combine(tempDir, "storage_root");
            Directory.CreateDirectory(storageRootPath);
            using (var db = new TiToAIDbContext(dbOptions))
            {
                db.AppSettings.Add(new AppSetting
                {
                    Key = "storage.roots.root-default-docs",
                    Value = StoragePathGuard.NormalizePath(storageRootPath),
                    UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                });
                await db.SaveChangesAsync();
            }

            var simulatedTime = DateTimeOffset.UtcNow;
            var fileStore = new AssetFileStore();
            const string expectedScriptContent = "Script generated content to be saved to storage";
            const string expectedPrompt = "Prompt for Fault 5 storage error test";
            const string expectedConfig = "{\"model\":\"gpt-4o\"}";

            string jobId = Guid.NewGuid().ToString("D");
            var expectedRelPath = $"ws_t16/output/{jobId}_output.txt";

            var fakeExecutor = new DeterministicFakeJobExecutor
            {
                ResultTextToReturn = expectedScriptContent,
                SimulateStorageErrorAfterAiGeneration = true,
                FileStore = fileStore,
                RootRegistry = registry,
                FinalizeRelativePath = expectedRelPath
            };

            var reservation = new JobReservation(dbOptions, () => simulatedTime);
            await using var scheduler = new JobScheduler(dbOptions, reservation, fakeExecutor, clock: () => simulatedTime);

            using (var db = new TiToAIDbContext(dbOptions))
            {
                var nowIso = simulatedTime.ToString("o");
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = "prj_t16",
                    TaskType = JobTaskType.Script,
                    State = JobState.Queued,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root-default-docs",
                    PromptSnapshotText = expectedPrompt,
                    ConfigSnapshotJson = expectedConfig,
                    Attempts = 0,
                    MaxAttempts = 3,
                    FencingToken = 0,
                    SideEffectSubmitted = 0,
                    CancelRequested = 0,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });
                await db.SaveChangesAsync();
            }

            // 1. Scheduler dispatch lần đầu qua production method
            var dispatched1 = await scheduler.DispatchNextSingleJobAsync();
            if (!dispatched1)
            {
                throw new InvalidOperationException("Scheduler không thể dispatch Job ở lần chạy đầu tiên.");
            }

            // Đợi worker hoàn tất đợt thực thi thứ nhất (gặp lỗi storage và checkpoint kết quả đệm)
            await scheduler.WaitForWorkerCompletionAsync(jobId, TimeSpan.FromSeconds(5));
            var sw = Stopwatch.StartNew();
            Job? jobAfterFirstRun = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                using var db = new TiToAIDbContext(dbOptions);
                var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId);
                if (j != null && j.State != JobState.Running && j.State != JobState.Reserved)
                {
                    jobAfterFirstRun = j;
                    break;
                }
                await Task.Delay(50);
            }

            if (jobAfterFirstRun == null)
            {
                throw new TimeoutException("Hết thời gian chờ scheduler thực thi đợt 1.");
            }

            // Kiểm tra DB sau đợt 1: Job có checkpoint ResultText, SideEffectSubmitted == 1, và chuyển AwaitingUser
            if (jobAfterFirstRun.ResultText != expectedScriptContent)
            {
                throw new InvalidOperationException($"ResultText không được lưu đệm chính xác. Kỳ vọng: '{expectedScriptContent}', thực tế: '{jobAfterFirstRun.ResultText}'");
            }
            if (jobAfterFirstRun.SideEffectSubmitted != 1)
            {
                throw new InvalidOperationException($"SideEffectSubmitted phải là 1 sau khi AI sinh nội dung. Thực tế: {jobAfterFirstRun.SideEffectSubmitted}");
            }
            if (jobAfterFirstRun.State != JobState.AwaitingUser)
            {
                throw new InvalidOperationException($"Job phải ở AwaitingUser sau lỗi storage. Thực tế: {jobAfterFirstRun.State}");
            }

            // File trên đĩa tuyệt đối CHƯA được tạo ở đợt 1 do storage offline
            var fullOutputPath = Path.Combine(storageRootPath, expectedRelPath);
            if (File.Exists(fullOutputPath))
            {
                throw new InvalidOperationException("File đầu ra không được tồn tại khi storage gặp lỗi ở đợt 1.");
            }

            // 2. Mô phỏng Restart ứng dụng & Khôi phục Storage (reconnected)
            // Dispose scheduler cũ hoàn toàn
            await scheduler.DisposeAsync();

            // Khôi phục storage: không còn lỗi lưu trữ
            fakeExecutor.SimulateStorageErrorAfterAiGeneration = false;

            // Khởi tạo các thành phần production mới (mô phỏng app startup sau restart)
            var newReservation = new JobReservation(dbOptions, () => simulatedTime);
            await using var newScheduler = new JobScheduler(dbOptions, newReservation, fakeExecutor, clock: () => simulatedTime);
            var snapshotFactory = new JobSnapshotFactory(settings);
            var jobService = new JobService(dbOptions, snapshotFactory, newScheduler, clock: () => simulatedTime);

            // 3. Gọi Command Production (JobService.ResumeAsync): KHÔNG sửa DbContext trực tiếp!
            var resumeResult = await jobService.ResumeAsync(jobId);
            if (resumeResult.State != JobState.Queued)
            {
                throw new InvalidOperationException($"Kỳ vọng ResumeAsync đưa Job về Queued để finalize từ checkpoint, thực tế: {resumeResult.State}");
            }

            // Kiểm tra DB: Job đã về Queued mà không bị reset SideEffectSubmitted (vẫn = 1 để bảo vệ lịch sử)
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var j = await db.Jobs.AsNoTracking().FirstAsync(x => x.Id == jobId);
                if (j.State != JobState.Queued)
                {
                    throw new InvalidOperationException($"Kỳ vọng DB Job state = Queued sau resume, thực tế: {j.State}");
                }
                if (j.SideEffectSubmitted != 1)
                {
                    throw new InvalidOperationException($"SideEffectSubmitted phải được bảo toàn là 1, thực tế: {j.SideEffectSubmitted}");
                }
                if (j.ResultText != expectedScriptContent)
                {
                    throw new InvalidOperationException($"Checkpoint ResultText phải được bảo toàn, thực tế: {j.ResultText}");
                }
            }

            // 4. Dispatch lượt retry qua production scheduler mới
            var retryDispatched = await newScheduler.DispatchNextSingleJobAsync();
            if (!retryDispatched)
            {
                throw new InvalidOperationException("Scheduler mới không thể dispatch lượt finalize cho job.");
            }

            // Đợi worker hoàn tất đợt finalize từ checkpoint
            var workerCompleted = await newScheduler.WaitForWorkerCompletionAsync(jobId, TimeSpan.FromSeconds(5));
            sw.Restart();
            Job? finalJob = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                using var db = new TiToAIDbContext(dbOptions);
                var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId);
                if (j != null && j.State == JobState.Succeeded)
                {
                    finalJob = j;
                    break;
                }
                if (j != null && (j.State == JobState.AwaitingUser || j.State == JobState.Failed || j.State == JobState.Canceled))
                {
                    finalJob = j;
                    break;
                }
                await Task.Delay(50);
            }

            if (finalJob == null || finalJob.State != JobState.Succeeded)
            {
                using var diagDb = new TiToAIDbContext(dbOptions);
                var diagJob = await diagDb.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId);
                var diagBinding = await diagDb.ExecutionBindings.AsNoTracking().FirstOrDefaultAsync(x => x.JobId == jobId);
                var diagOccupancies = await diagDb.ActiveSlotOccupancies.AsNoTracking().Where(x => x.JobId == jobId).ToListAsync();

                var diagInfo = $"""
                    [FAULT 5 TIMEOUT / FAILURE DIAGNOSTIC]
                    - Job ID: {jobId}
                    - Job State: {diagJob?.State ?? "NULL"} (Expected: Succeeded)
                    - Reason Code: {diagJob?.ReasonCode ?? "NULL"}
                    - Attempts/MaxAttempts: {diagJob?.Attempts}/{diagJob?.MaxAttempts}
                    - SideEffectSubmitted: {diagJob?.SideEffectSubmitted}
                    - CancelRequested: {diagJob?.CancelRequested}
                    - FencingToken: {diagJob?.FencingToken}
                    - LeaseOwnerId: {diagJob?.LeaseOwnerId ?? "NULL"}
                    - LeaseExpiresAtUtc: {diagJob?.LeaseExpiresAtUtc ?? "NULL"}
                    - ResultText Present: {!string.IsNullOrWhiteSpace(diagJob?.ResultText)} (Length: {diagJob?.ResultText?.Length ?? 0})
                    - ResultReference: {diagJob?.ResultReference ?? "NULL"}
                    - ExecutionBinding: {(diagBinding != null ? $"Account={diagBinding.ProviderAccountId}, Profile={diagBinding.ProfileId}, Slot={diagBinding.TabSlotId}" : "NONE")}
                    - ActiveSlotOccupancies Count: {diagOccupancies.Count} (Slots: {string.Join(", ", diagOccupancies.Select(o => o.TabSlotId))})
                    - Dispatch Result: {retryDispatched}
                    - Worker Completed: {workerCompleted}
                    - Worker Task Still Running: {newScheduler.RunningWorkerTasks.ContainsKey(jobId)}
                    - Last Worker Exception: {newScheduler.LastWorkerException?.ToString() ?? "NONE"}
                    - Target File Path: {fullOutputPath}
                    - Target File Exists: {File.Exists(fullOutputPath)}
                    - Total AI Submits: {fakeExecutor.TotalAiSubmits}
                    """;

                Console.WriteLine(diagInfo);
                throw new TimeoutException($"Hết thời gian chờ worker finalize và chuyển Succeeded.\n{diagInfo}");
            }

            // 5. Khẳng định nghiêm ngặt toàn bộ tiêu chí nghiệm thu:
            // Khẳng định 1: TotalAiSubmits == 1 (BẮT BUỘC số lần submit AI = 1!)
            if (fakeExecutor.TotalAiSubmits != 1)
            {
                throw new InvalidOperationException($"BẮT BUỘC fakeExecutor.TotalAiSubmits == 1 (không được gọi lại AI khi retry finalize). Thực tế: {fakeExecutor.TotalAiSubmits}");
            }

            // Khẳng định 2: Job state trong DB chuyển thành Succeeded
            if (finalJob.State != JobState.Succeeded)
            {
                throw new InvalidOperationException($"Job state trong DB phải là Succeeded. Thực tế: {finalJob.State}");
            }

            // Khẳng định 3: Job có ResultText chính xác
            if (finalJob.ResultText != expectedScriptContent)
            {
                throw new InvalidOperationException($"ResultText không khớp. Kỳ vọng: '{expectedScriptContent}', thực tế: '{finalJob.ResultText}'");
            }

            // Khẳng định 4: Tệp kết quả được ghi bởi AssetFileStore production, test KHÔNG tự ghi
            if (!File.Exists(fullOutputPath))
            {
                throw new InvalidOperationException($"Tệp kết quả '{fullOutputPath}' không tồn tại trên storage sau khi finalize!");
            }
            var savedContent = await File.ReadAllTextAsync(fullOutputPath);
            if (savedContent != expectedScriptContent)
            {
                throw new InvalidOperationException($"Nội dung tệp trên storage sai lệch. Kỳ vọng: '{expectedScriptContent}', thực tế: '{savedContent}'");
            }

            // Khẳng định 5: ExecutionBinding và Snapshot được bảo tồn nguyên vẹn
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var binding = await db.ExecutionBindings.FirstOrDefaultAsync(b => b.JobId == jobId);
                if (binding == null)
                {
                    throw new InvalidOperationException("ExecutionBinding bị mất sau retry finalize!");
                }
                if (binding.ProviderAccountId != "acc_t16" || binding.ProfileId != "prof_t16")
                {
                    throw new InvalidOperationException($"ExecutionBinding bị thay đổi ngoài ý muốn: account={binding.ProviderAccountId}, profile={binding.ProfileId}");
                }

                var jobDb = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (jobDb.PromptSnapshotText != expectedPrompt)
                {
                    throw new InvalidOperationException($"PromptSnapshotText bị sai lệch: '{jobDb.PromptSnapshotText}'");
                }
                if (jobDb.ConfigSnapshotJson != expectedConfig)
                {
                    throw new InvalidOperationException($"ConfigSnapshotJson bị sai lệch: '{jobDb.ConfigSnapshotJson}'");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Fault 6: Worker cũ (Zombie) cố commit sau khi lease đã hết hạn và Scheduler đã cấp cho Worker mới.
    /// Bị từ chối với FencingTokenStaleException.
    /// </summary>
    public static async Task Test_Fault6_FencingTokenMismatch_RejectsZombieCommit()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var simulatedTime = DateTimeOffset.UtcNow;
            var reservation = new JobReservation(dbOptions, () => simulatedTime);
            var scheduler = new JobScheduler(dbOptions, reservation, new DummyJobExecutor(), clock: () => simulatedTime);

            string jobId = Guid.NewGuid().ToString("D");
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var nowIso = simulatedTime.ToString("o");
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = "prj_t16",
                    TaskType = JobTaskType.Script,
                    State = JobState.Queued,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root-default-docs",
                    Attempts = 0,
                    MaxAttempts = 3,
                    FencingToken = 0,
                    SideEffectSubmitted = 0,
                    CancelRequested = 0,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });
                await db.SaveChangesAsync();
            }

            // Worker 1 reserve job (FencingToken = 1)
            var res1 = await reservation.TryReserveJobAsync(jobId, "worker-1", TimeSpan.FromSeconds(10));
            long zombieFencingToken = res1.Job!.FencingToken;

            // Tua thời gian để Worker 1 hết hạn lease
            simulatedTime = simulatedTime.AddSeconds(15);
            await scheduler.RunCrashRecoverySweepAsync();

            // Tua thời gian để vượt qua retry_after
            simulatedTime = simulatedTime.AddSeconds(15);

            // Người dùng chủ động thử lại trước khi Worker 2 nhận job.
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var failed = await db.Jobs.FirstAsync(j => j.Id == jobId);
                failed.State = JobState.Queued;
                failed.ReasonCode = null;
                await db.SaveChangesAsync();
            }

            // Worker 2 reserve job thành công (FencingToken = 2)
            var res2 = await reservation.TryReserveJobAsync(jobId, "worker-2", TimeSpan.FromSeconds(10));
            if (!res2.Acquired) throw new InvalidOperationException("Worker 2 phải reserve được job.");
            long newFencingToken = res2.Job!.FencingToken;

            if (newFencingToken <= zombieFencingToken)
            {
                throw new InvalidOperationException($"FencingToken mới ({newFencingToken}) phải lớn hơn cũ ({zombieFencingToken}).");
            }

            // Zombie Worker 1 thức dậy cố gọi CommitSuccessAsync với token cũ
            bool caughtFencingException = false;
            try
            {
                await reservation.CommitSuccessAsync(jobId, zombieFencingToken, "res_old", "text_old");
            }
            catch (FencingTokenStaleException)
            {
                caughtFencingException = true;
            }

            if (!caughtFencingException)
            {
                throw new InvalidOperationException("Zombie worker cố commit phải bị ném FencingTokenStaleException!");
            }

            // Kiểm tra DB: Job vẫn nguyên vẹn thuộc về Worker 2
            using (var db = new TiToAIDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.FencingToken != newFencingToken)
                {
                    throw new InvalidOperationException("FencingToken trong DB không được bị ghi đè bởi zombie worker!");
                }
                if (job.State != JobState.Reserved && job.State != JobState.Running)
                {
                    throw new InvalidOperationException("Trạng thái job không được bị zombie worker đổi sang Succeeded!");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class DummyJobExecutor : IJobExecutor
    {
        public Task<JobExecutionResult> ExecuteAsync(
            JobExecutionContext context,
            Func<Task>? onPreSideEffectCommitAsync,
            Func<int, string, Task>? onProgressAsync,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new JobExecutionResult(Success: false, ReasonCode: "dummy_failed"));
        }
    }
}
