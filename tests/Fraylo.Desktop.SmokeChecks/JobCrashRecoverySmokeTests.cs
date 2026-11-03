using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Jobs;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói định lượng Task T16:
/// Job Crash Recovery & Reconcile Engine.
/// Bao phủ 9 ca kiểm thử SQLite thật với DB tạm:
/// 1. Crash trước submit (SideEffectSubmitted=0): dừng ở Failed, không tự retry và xóa occupancy.
/// 2. Crash trước submit nhưng hết attempts: phục hồi sang Failed (lease_expired_max_attempts).
/// 3. Crash sau submit (SideEffectSubmitted=1): phục hồi sang AwaitingUser (reconcile_required) và GIỮ NGUYÊN slot occupancy.
/// 4. Hai worker đồng thời chạy recovery sweep trên 1 job: đúng 1 winner thành công, loser không corrupt state.
/// 5. Reconcile resubmit khi SideEffectSubmitted=1 không có ConfirmedNotReceived bị từ chối; có ConfirmedNotReceived=true được đưa về Queued an toàn.
/// 6. Reconcile mark_succeeded không có kết quả bị từ chối; có kết quả chuyển Succeeded và xóa occupancy.
/// 7. Reconcile mark_failed và cancel hoạt động đúng và xóa occupancy.
/// 8. Restart quét DB phục hồi giữ nguyên ExecutionBinding và snapshot.
/// 9. Acceptance 2 capacity=1: Submit -> Lease Expired -> AwaitingUser -> Cancel unresolved side effect giữ occupancy chặn Job mới cho tới khi đối soát an toàn.
/// </summary>
public static class JobCrashRecoverySmokeTests
{
    private static (DbContextOptions<FrayloDbContext> Options, string TempDir) CreateTestContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"Fraylo_crash_recovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "Fraylo_test.db");
        var connectionString = DatabasePathHelper.BuildConnectionString(dbPath);

        var optionsBuilder = new DbContextOptionsBuilder<FrayloDbContext>();
        optionsBuilder.UseSqlite(connectionString)
                      .AddInterceptors(new SqlitePragmaInterceptor())
                      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        var dbOptions = optionsBuilder.Options;

        using (var db = new FrayloDbContext(dbOptions))
        {
            db.Database.EnsureCreated();

            var now = DateTimeOffset.UtcNow.ToString("o");
            if (!db.BrowserProfiles.Any(p => p.Id == "shared_ai"))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "shared_ai",
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "ChatGPT Pool Account",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }

            if (!db.ProviderAccounts.Any(a => a.ProfileId == "shared_ai"))
            {
                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "account-chatgpt-default",
                    ProviderKey = "chatgpt-web",
                    ProfileId = "shared_ai",
                    DisplayName = "ChatGPT Pool Account",
                    VerifiedIdentity = "user@chatgpt.test",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 3,
                    MaxTabs = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }

            db.SaveChanges();
        }

        return (dbOptions, tempDir);
    }

    private static void CleanupTestContext(string tempDir)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Bỏ qua lỗi dọn dẹp thư mục tạm
        }
    }

    private static async Task<(string ProjectId, string SceneId)> SeedBasicEntitiesAsync(
        DbContextOptions<FrayloDbContext> dbOptions)
    {
        await using var db = new FrayloDbContext(dbOptions);


        var prjId = Guid.NewGuid().ToString();
        var scnId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToString("o");


        var prj = new Project
        {
            Id = prjId,
            Name = "Project Crash Test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Projects.Add(prj);

        var scn = new Scene
        {
            Id = scnId,
            ProjectId = prjId,
            SequenceOrder = 1,
            Name = "Scene 1",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Scenes.Add(scn);

        await db.SaveChangesAsync();
        return (prjId, scnId);
    }

    /// <summary>
    /// Test 1: Crash trước submit (SideEffectSubmitted=0): dừng ở Failed, không tự retry và xóa occupancy.
    /// </summary>
    public static async Task Test_CrashBeforeSideEffect_StopsAndDeletesOccupancy()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var jobId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var expiredLease = now.AddMinutes(-5).ToString("o");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = prjId,
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Running,
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    LeaseOwnerId = "worker-crash-1",
                    LeaseExpiresAtUtc = expiredLease,
                    FencingToken = 10,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = "account-chatgpt-default",
                    TabSlotId = "slot-1",
                    JobId = jobId,
                    LeaseOwnerId = "worker-crash-1",
                    LeaseToken = 10,
                    AcquiredAtUtc = nowIso
                });

                await db.SaveChangesAsync();
            }

            var recoveryService = new JobCrashRecoveryService(dbOptions, clock: () => now);
            var sweepResult = await recoveryService.PerformRecoverySweepAsync();

            if (sweepResult.RecoveredCount != 1)
                throw new Exception($"Kỳ vọng 1 job recovered, thực tế: {sweepResult.RecoveredCount}");
            if (sweepResult.RetriedCount != 0 || sweepResult.FailedCount != 1)
                throw new Exception($"Kỳ vọng 0 job tự retry và 1 job Failed, thực tế: {sweepResult.RetriedCount}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.Failed)
                    throw new Exception($"Kỳ vọng trạng thái Failed, thực tế: {job.State}");
                if (job.LeaseExpiresAtUtc != null)
                    throw new Exception($"Kỳ vọng LeaseExpiresAtUtc = null, thực tế: {job.LeaseExpiresAtUtc}");
                if (job.LeaseOwnerId != null)
                    throw new Exception($"Kỳ vọng LeaseOwnerId = null, thực tế: {job.LeaseOwnerId}");
                if (job.RetryAfterUtc != null)
                    throw new Exception("Job lỗi không được hẹn tự thử lại.");

                var occExists = await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == jobId);
                if (occExists)
                    throw new Exception("Kỳ vọng ActiveSlotOccupancy đã bị xóa khỏi DB.");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 2: Crash trước submit nhưng hết attempts (Attempts >= MaxAttempts): phục hồi sang Failed (lease_expired_max_attempts).
    /// </summary>
    public static async Task Test_LegacyAutoRetry_StopsButManualAndNewJobsRemainQueued()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (projectId, _) = await SeedBasicEntitiesAsync(dbOptions);
            var now = DateTimeOffset.UtcNow;
            await using (var db = new FrayloDbContext(dbOptions))
            {
                foreach (var id in new[] { "auto-retry", "manual-retry", "new-job" })
                    db.Jobs.Add(new Job
                    {
                        Id = id, ProjectId = projectId, TaskType = JobTaskType.Script,
                        State = JobState.Queued, Attempts = id == "new-job" ? 0 : 1, MaxAttempts = 3,
                        RetryAfterUtc = id == "auto-retry" ? now.AddMinutes(5).ToString("o") : null,
                        ProviderKey = "chatgpt-web", StorageRootId = "root-docs",
                        CreatedAtUtc = now.ToString("o"), UpdatedAtUtc = now.ToString("o")
                    });
                await db.SaveChangesAsync();
            }
            var recovery = new JobCrashRecoveryService(dbOptions, clock: () => now);
            var result = await recovery.PerformRecoverySweepAsync();
            if (result.FailedCount != 1 || result.RetriedCount != 0)
                throw new Exception("Phải dừng đúng một lượt tự retry cũ.");
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var autoRetry = await db.Jobs.SingleAsync(j => j.Id == "auto-retry");
                if (autoRetry.State != JobState.Failed || autoRetry.RetryAfterUtc != null)
                    throw new Exception("Lượt tự retry cũ phải dừng.");
                if (await db.Jobs.CountAsync(j => j.State == JobState.Queued) != 2)
                    throw new Exception("Job mới và retry thủ công phải giữ Queued.");
            }
            if ((await recovery.PerformRecoverySweepAsync()).RecoveredCount != 0)
                throw new Exception("Quét lại không được thay đổi job đã dừng.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }
    public static async Task Test_CrashBeforeSideEffect_MaxAttemptsExceeded_RecoversToFailedAndDeletesOccupancy()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var jobId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var expiredLease = now.AddMinutes(-5).ToString("o");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = prjId,
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Reserved,
                    Attempts = 3,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    LeaseOwnerId = "worker-crash-2",
                    LeaseExpiresAtUtc = expiredLease,
                    FencingToken = 20,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = "account-chatgpt-default",
                    TabSlotId = "slot-2",
                    JobId = jobId,
                    LeaseOwnerId = "worker-crash-2",
                    LeaseToken = 20,
                    AcquiredAtUtc = nowIso
                });

                await db.SaveChangesAsync();
            }

            var recoveryService = new JobCrashRecoveryService(dbOptions, clock: () => now);
            var sweepResult = await recoveryService.PerformRecoverySweepAsync();

            if (sweepResult.RecoveredCount != 1)
                throw new Exception($"Kỳ vọng 1 job recovered, thực tế: {sweepResult.RecoveredCount}");
            if (sweepResult.FailedCount != 1)
                throw new Exception($"Kỳ vọng 1 job failed, thực tế: {sweepResult.FailedCount}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.Failed)
                    throw new Exception($"Kỳ vọng trạng thái Failed, thực tế: {job.State}");
                if (job.ReasonCode != JobReasonCode.LeaseExpiredMaxAttempts)
                    throw new Exception($"Kỳ vọng ReasonCode = lease_expired_max_attempts, thực tế: {job.ReasonCode}");
                if (job.LeaseExpiresAtUtc != null)
                    throw new Exception($"Kỳ vọng LeaseExpiresAtUtc = null, thực tế: {job.LeaseExpiresAtUtc}");

                var occExists = await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == jobId);
                if (occExists)
                    throw new Exception("Kỳ vọng ActiveSlotOccupancy đã bị xóa khi đạt max attempts.");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 3: Crash sau submit (SideEffectSubmitted=1): phục hồi sang AwaitingUser (reconcile_required) và GIỮ NGUYÊN slot occupancy.
    /// </summary>
    public static async Task Test_CrashAfterSideEffect_RecoversToAwaitingUserAndPreservesOccupancy()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var jobId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var expiredLease = now.AddMinutes(-5).ToString("o");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = prjId,
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Running,
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 1,
                    SideEffectAtUtc = now.AddMinutes(-6).ToString("o"),
                    LeaseOwnerId = "worker-crash-3",
                    LeaseExpiresAtUtc = expiredLease,
                    FencingToken = 30,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = "account-chatgpt-default",
                    TabSlotId = "slot-3",
                    JobId = jobId,
                    LeaseOwnerId = "worker-crash-3",
                    LeaseToken = 30,
                    AcquiredAtUtc = nowIso
                });

                await db.SaveChangesAsync();
            }

            var recoveryService = new JobCrashRecoveryService(dbOptions, clock: () => now);
            var sweepResult = await recoveryService.PerformRecoverySweepAsync();

            if (sweepResult.RecoveredCount != 1)
                throw new Exception($"Kỳ vọng 1 job recovered, thực tế: {sweepResult.RecoveredCount}");
            if (sweepResult.MovedToReconcileCount != 1)
                throw new Exception($"Kỳ vọng 1 job moved to reconcile, thực tế: {sweepResult.MovedToReconcileCount}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.AwaitingUser)
                    throw new Exception($"Kỳ vọng trạng thái AwaitingUser, thực tế: {job.State}");
                if (job.ReasonCode != JobReasonCode.ReconcileRequired)
                    throw new Exception($"Kỳ vọng ReasonCode = reconcile_required, thực tế: {job.ReasonCode}");
                if (job.SideEffectSubmitted != 1)
                    throw new Exception($"Kỳ vọng SideEffectSubmitted = 1, thực tế: {job.SideEffectSubmitted}");

                // BẢO TỒN OCCUPANCY: ActiveSlotOccupancy PHẢI VẪN CÒN NGUYÊN!
                var occ = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == jobId);
                if (occ == null)
                    throw new Exception("Kỳ vọng ActiveSlotOccupancy được bảo tồn nguyên vẹn để bảo vệ slot/tab!");
                if (occ.TabSlotId != "slot-3")
                    throw new Exception($"Kỳ vọng TabSlotId = slot-3, thực tế: {occ.TabSlotId}");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 4: Hai worker đồng thời chạy recovery sweep trên 1 job: đúng 1 winner thành công, loser không corrupt state.
    /// </summary>
    public static async Task Test_ConcurrentRecoverySweeps_ExactlyOneWinnerNoCorruption()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var jobId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var expiredLease = now.AddMinutes(-5).ToString("o");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = prjId,
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Running,
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    LeaseOwnerId = "worker-racing",
                    LeaseExpiresAtUtc = expiredLease,
                    FencingToken = 40,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = "account-chatgpt-default",
                    TabSlotId = "slot-1",
                    JobId = jobId,
                    LeaseOwnerId = "worker-racing",
                    LeaseToken = 40,
                    AcquiredAtUtc = nowIso
                });

                await db.SaveChangesAsync();
            }

            var service1 = new JobCrashRecoveryService(dbOptions, clock: () => now);
            var service2 = new JobCrashRecoveryService(dbOptions, clock: () => now);

            var task1 = Task.Run(() => service1.PerformRecoverySweepAsync());
            var task2 = Task.Run(() => service2.PerformRecoverySweepAsync());

            var results = await Task.WhenAll(task1, task2);

            int totalRecovered = results[0].RecoveredCount + results[1].RecoveredCount;
            if (totalRecovered != 1)
            {
                throw new Exception($"Kỳ vọng đúng 1 winner phục hồi job, thực tế: {totalRecovered} (Winner1: {results[0].RecoveredCount}, Winner2: {results[1].RecoveredCount})");
            }

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.Failed)
                    throw new Exception($"Kỳ vọng trạng thái Failed sau sweep, thực tế: {job.State}");

                var occExists = await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == jobId);
                if (occExists)
                    throw new Exception("Kỳ vọng ActiveSlotOccupancy đã được xóa sạch.");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 5: Reconcile resubmit khi SideEffectSubmitted=1 không có ConfirmedNotReceived bị từ chối; có ConfirmedNotReceived=true được đưa về Queued an toàn.
    /// </summary>
    public static async Task Test_ReconcileResubmit_RequiresConfirmedNotReceivedWhenSideEffectSubmitted()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var jobId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = new Job
                {
                    Id = jobId,
                    ProjectId = prjId,
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.AwaitingUser,
                    ReasonCode = JobReasonCode.ReconcileRequired,
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 1,
                    SideEffectAtUtc = nowIso,
                    PromptSnapshotText = "Sample Prompt",
                    ConfigSnapshotJson = "{\"temperature\":0.7}",
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                };
                db.Jobs.Add(job);

                db.ExecutionBindings.Add(new ExecutionBinding
                {
                    JobId = jobId,
                    ProviderAccountId = "account-chatgpt-default",
                    ProfileId = "shared_ai",
                    TabSlotId = "slot-1",
                    CapacityConfigRevision = 1,
                    AssignedAtUtc = nowIso
                });

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = "account-chatgpt-default",
                    TabSlotId = "slot-1",
                    JobId = jobId,
                    LeaseOwnerId = "worker-1",
                    LeaseToken = 1,
                    AcquiredAtUtc = nowIso
                });

                await db.SaveChangesAsync();
            }

            var reconcileService = new JobReconciliationService(dbOptions, clock: () => now);

            // 5a. Thử resubmit khi KHÔNG có ConfirmedNotReceived => Phải ném ngoại lệ JobValidationException
            bool threwValidationException = false;
            try
            {
                await reconcileService.ReconcileAsync(new JobReconcileRequest(
                    JobId: jobId,
                    Action: "retry",
                    ConfirmedNotReceived: false
                ));
            }
            catch (JobValidationException)
            {
                threwValidationException = true;
            }

            if (!threwValidationException)
            {
                throw new Exception("Kỳ vọng ném JobValidationException khi resubmit job đã submit side effect mà ConfirmedNotReceived != true!");
            }

            // Kiểm tra state không đổi
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.AwaitingUser)
                    throw new Exception($"Kỳ vọng job vẫn ở AwaitingUser, thực tế: {job.State}");
                if (job.SideEffectSubmitted != 1)
                    throw new Exception("Kỳ vọng SideEffectSubmitted vẫn là 1.");
            }

            // 5b. Thử resubmit khi CÓ ConfirmedNotReceived == true => Thành công, đưa về Queued
            var res = await reconcileService.ReconcileAsync(new JobReconcileRequest(
                JobId: jobId,
                Action: "retry",
                ConfirmedNotReceived: true
            ));

            if (res.CurrentState != JobState.Queued)
                throw new Exception($"Kỳ vọng CurrentState = Queued, thực tế: {res.CurrentState}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.Queued)
                    throw new Exception($"Kỳ vọng DB state = Queued, thực tế: {job.State}");
                if (job.SideEffectSubmitted != 0)
                    throw new Exception("Kỳ vọng SideEffectSubmitted đã được reset về 0.");
                if (job.ReasonCode != null)
                    throw new Exception($"Kỳ vọng ReasonCode = null, thực tế: {job.ReasonCode}");

                // Binding và snapshot vẫn nguyên vẹn
                var binding = await db.ExecutionBindings.FirstAsync(b => b.JobId == jobId);
                if (binding.ProviderAccountId != "account-chatgpt-default" || binding.ProfileId != "shared_ai")
                    throw new Exception("ExecutionBinding bị thay đổi ngoài ý muốn.");
                if (job.PromptSnapshotText != "Sample Prompt")
                    throw new Exception("PromptSnapshotText bị thay đổi ngoài ý muốn.");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 6: Reconcile mark_succeeded không có kết quả bị từ chối; có kết quả chuyển Succeeded và xóa occupancy.
    /// </summary>
    public static async Task Test_ReconcileMarkSucceeded_RequiresValidResultAndReleasesOccupancy()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var jobId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = prjId,
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.AwaitingUser,
                    ReasonCode = JobReasonCode.ReconcileRequired,
                    SideEffectSubmitted = 1,
                    ResultReference = null,
                    ResultText = null,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = "account-chatgpt-default",
                    TabSlotId = "slot-1",
                    JobId = jobId,
                    LeaseOwnerId = "worker-1",
                    LeaseToken = 1,
                    AcquiredAtUtc = nowIso
                });

                await db.SaveChangesAsync();
            }

            var reconcileService = new JobReconciliationService(dbOptions, clock: () => now);

            // 6a. mark_succeeded không truyền kết quả và job chưa có kết quả => Phải ném ngoại lệ
            bool threwValidationException = false;
            try
            {
                await reconcileService.ReconcileAsync(new JobReconcileRequest(
                    JobId: jobId,
                    Action: "mark_succeeded",
                    ResultReference: "   ",
                    ResultText: ""
                ));
            }
            catch (JobValidationException)
            {
                threwValidationException = true;
            }

            if (!threwValidationException)
            {
                throw new Exception("Kỳ vọng ném JobValidationException khi mark_succeeded không có kết quả hợp lệ!");
            }

            // 6b. mark_succeeded có ResultReference hợp lệ => Thành công, chuyển Succeeded, xóa Occupancy
            var res = await reconcileService.ReconcileAsync(new JobReconcileRequest(
                JobId: jobId,
                Action: "mark_succeeded",
                ResultReference: "asset-rec-result-999",
                ResultText: "Resolved by human verification"
            ));

            if (res.CurrentState != JobState.Succeeded)
                throw new Exception($"Kỳ vọng CurrentState = Succeeded, thực tế: {res.CurrentState}");
            if (res.ResultReference != "asset-rec-result-999")
                throw new Exception($"Kỳ vọng ResultReference = asset-rec-result-999, thực tế: {res.ResultReference}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.Succeeded)
                    throw new Exception($"Kỳ vọng DB state = Succeeded, thực tế: {job.State}");
                if (job.ResultReference != "asset-rec-result-999")
                    throw new Exception($"Kỳ vọng DB ResultReference = asset-rec-result-999, thực tế: {job.ResultReference}");
                if (job.ResultText != "Resolved by human verification")
                    throw new Exception($"Kỳ vọng DB ResultText khớp giá trị đã nhập, thực tế: {job.ResultText}");

                var occExists = await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == jobId);
                if (occExists)
                    throw new Exception("Kỳ vọng ActiveSlotOccupancy đã được giải phóng khi mark_succeeded.");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 7: Reconcile mark_failed và cancel hoạt động đúng và xóa occupancy.
    /// </summary>
    public static async Task Test_ReconcileMarkFailedAndCancel_WorksProperlyAndReleasesOccupancy()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var job1Id = Guid.NewGuid().ToString();
            var job2Id = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.AddRange(
                    new Job
                    {
                        Id = job1Id,
                        ProjectId = prjId,
                        SceneId = null,
                        TaskType = JobTaskType.Script,
                        State = JobState.AwaitingUser,
                        ReasonCode = JobReasonCode.ReconcileRequired,
                        CreatedAtUtc = nowIso,
                        UpdatedAtUtc = nowIso
                    },
                    new Job
                    {
                        Id = job2Id,
                        ProjectId = prjId,
                        SceneId = null,
                        TaskType = JobTaskType.Script,
                        State = JobState.AwaitingUser,
                        ReasonCode = JobReasonCode.ReconcileRequired,
                        CreatedAtUtc = nowIso,
                        UpdatedAtUtc = nowIso
                    }
                );

                db.ActiveSlotOccupancies.AddRange(
                    new ActiveSlotOccupancy
                    {
                        ProviderAccountId = "account-chatgpt-default",
                        TabSlotId = "slot-1",
                        JobId = job1Id,
                        LeaseOwnerId = "worker-1",
                        LeaseToken = 1,
                        AcquiredAtUtc = nowIso
                    },
                    new ActiveSlotOccupancy
                    {
                        ProviderAccountId = "account-chatgpt-default",
                        TabSlotId = "slot-2",
                        JobId = job2Id,
                        LeaseOwnerId = "worker-2",
                        LeaseToken = 1,
                        AcquiredAtUtc = nowIso
                    }
                );

                await db.SaveChangesAsync();
            }

            var reconcileService = new JobReconciliationService(dbOptions, clock: () => now);

            // 7a. mark_failed trên job1
            var res1 = await reconcileService.ReconcileAsync(new JobReconcileRequest(
                JobId: job1Id,
                Action: "mark_failed"
            ));

            if (res1.CurrentState != JobState.Failed)
                throw new Exception($"Kỳ vọng Job 1 CurrentState = Failed, thực tế: {res1.CurrentState}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job1 = await db.Jobs.FirstAsync(j => j.Id == job1Id);
                if (job1.State != JobState.Failed)
                    throw new Exception($"Kỳ vọng DB Job 1 state = Failed, thực tế: {job1.State}");
                if (job1.ReasonCode != JobReasonCode.ReconcileRejected)
                    throw new Exception($"Kỳ vọng ReasonCode = reconcile_rejected, thực tế: {job1.ReasonCode}");

                var occ1Exists = await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == job1Id);
                if (occ1Exists)
                    throw new Exception("Kỳ vọng ActiveSlotOccupancy của Job 1 đã được giải phóng.");
            }

            // 7b. cancel trên job2
            var res2 = await reconcileService.ReconcileAsync(new JobReconcileRequest(
                JobId: job2Id,
                Action: "cancel"
            ));

            if (res2.CurrentState != JobState.Canceled)
                throw new Exception($"Kỳ vọng Job 2 CurrentState = Canceled, thực tế: {res2.CurrentState}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job2 = await db.Jobs.FirstAsync(j => j.Id == job2Id);
                if (job2.State != JobState.Canceled)
                    throw new Exception($"Kỳ vọng DB Job 2 state = Canceled, thực tế: {job2.State}");
                if (job2.ReasonCode != JobReasonCode.UserCanceled)
                    throw new Exception($"Kỳ vọng ReasonCode = user_canceled, thực tế: {job2.ReasonCode}");

                var occ2Exists = await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == job2Id);
                if (occ2Exists)
                    throw new Exception("Kỳ vọng ActiveSlotOccupancy của Job 2 đã được giải phóng.");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 8: Restart quét DB phục hồi giữ nguyên ExecutionBinding và snapshot.
    /// </summary>
    public static async Task Test_RestartStartupRecovery_PreservesBindingAndSnapshots()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var jobId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var expiredLease = now.AddMinutes(-10).ToString("o");

            const string expectedPrompt = "Create a modern cinematic scene for Fraylo introduction.";
            const string expectedConfig = "{\"aspect_ratio\":\"16:9\",\"fps\":30,\"model\":\"gpt-4o-render\"}";

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = new Job
                {
                    Id = jobId,
                    ProjectId = prjId,
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Running,
                    Attempts = 2,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 1,
                    SideEffectAtUtc = now.AddMinutes(-12).ToString("o"),
                    LeaseOwnerId = "dead-instance-worker",
                    LeaseExpiresAtUtc = expiredLease,
                    FencingToken = 99,
                    PromptSnapshotText = expectedPrompt,
                    ConfigSnapshotJson = expectedConfig,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                };
                db.Jobs.Add(job);

                db.ExecutionBindings.Add(new ExecutionBinding
                {
                    JobId = jobId,
                    ProviderAccountId = "account-chatgpt-default",
                    ProfileId = "shared_ai",
                    TabSlotId = "slot-2",
                    CapacityConfigRevision = 42,
                    AssignedAtUtc = nowIso,
                    LeaseOwnerId = "dead-instance-worker",
                    LeaseToken = 99
                });

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = "account-chatgpt-default",
                    TabSlotId = "slot-2",
                    JobId = jobId,
                    LeaseOwnerId = "dead-instance-worker",
                    LeaseToken = 99,
                    AcquiredAtUtc = nowIso
                });

                await db.SaveChangesAsync();
            }

            // Mô phỏng restart ứng dụng: Tạo instance mới hoàn toàn của service và gọi PerformStartupRecoveryAsync
            var startupRecoveryService = new JobCrashRecoveryService(dbOptions, clock: () => now);
            var sweepResult = await startupRecoveryService.PerformStartupRecoveryAsync();

            if (sweepResult.RecoveredCount != 1)
                throw new Exception($"Kỳ vọng 1 job recovered khi startup, thực tế: {sweepResult.RecoveredCount}");
            if (sweepResult.MovedToReconcileCount != 1)
                throw new Exception($"Kỳ vọng 1 job moved to reconcile, thực tế: {sweepResult.MovedToReconcileCount}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
                if (job.State != JobState.AwaitingUser)
                    throw new Exception($"Kỳ vọng trạng thái AwaitingUser, thực tế: {job.State}");
                if (job.ReasonCode != JobReasonCode.ReconcileRequired)
                    throw new Exception($"Kỳ vọng ReasonCode = reconcile_required, thực tế: {job.ReasonCode}");

                // KIỂM TRA BẤT BIẾN: ExecutionBinding và Snapshot được bảo tồn nguyên vẹn
                if (job.PromptSnapshotText != expectedPrompt)
                    throw new Exception($"PromptSnapshotText bị sai lệch! Kỳ vọng: '{expectedPrompt}', thực tế: '{job.PromptSnapshotText}'");
                if (job.ConfigSnapshotJson != expectedConfig)
                    throw new Exception($"ConfigSnapshotJson bị sai lệch! Kỳ vọng: '{expectedConfig}', thực tế: '{job.ConfigSnapshotJson}'");

                var binding = await db.ExecutionBindings.FirstOrDefaultAsync(b => b.JobId == jobId);
                if (binding == null)
                    throw new Exception("ExecutionBinding bị mất sau recovery!");
                if (binding.ProviderAccountId != "account-chatgpt-default")
                    throw new Exception($"ProviderAccountId sai lệch: {binding.ProviderAccountId}");
                if (binding.ProfileId != "shared_ai")
                    throw new Exception($"ProfileId sai lệch: {binding.ProfileId}");
                if (binding.TabSlotId != "slot-2")
                    throw new Exception($"TabSlotId sai lệch: {binding.TabSlotId}");

                // ActiveSlotOccupancy vẫn còn nguyên
                var occ = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == jobId);
                if (occ == null)
                    throw new Exception("ActiveSlotOccupancy bị mất sau startup recovery!");
                if (occ.TabSlotId != "slot-2")
                    throw new Exception($"Occupancy TabSlotId sai lệch: {occ.TabSlotId}");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 9: Acceptance 2 capacity=1: Submit -> Lease Expired -> AwaitingUser -> Cancel unresolved side effect
    /// giữ nguyên ActiveSlotOccupancy và chặn Job mới cho tới khi đối soát an toàn (ConfirmedNotReceived == true).
    /// </summary>
    public static async Task Test9_Capacity1_Submit_LeaseExpired_AwaitingUser_Cancel_BlocksNewJobUntilReconciled()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var expiredLease = now.AddMinutes(-10).ToString("o");

            const string accountId = "account-chatgpt-default";
            const string profileId = "shared_ai";

            // 1. Cấu hình ProviderAccount với MaxConcurrentJobs = 1, MaxTabs = 1
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var account = await db.ProviderAccounts.FirstAsync(a => a.Id == accountId);
                account.MaxConcurrentJobs = 1;
                account.MaxTabs = 1;
                await db.SaveChangesAsync();
            }

            // 2. Job 1 chạy và submit side-effect (SideEffectSubmitted = 1)
            var job1Id = Guid.NewGuid().ToString();
            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = job1Id,
                    ProjectId = prjId,
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    ProviderKey = "chatgpt-web",
                    State = JobState.Running,
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 1,
                    SideEffectAtUtc = now.AddMinutes(-15).ToString("o"),
                    LeaseOwnerId = "worker-1",
                    LeaseExpiresAtUtc = expiredLease,
                    FencingToken = 10,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });

                db.ExecutionBindings.Add(new ExecutionBinding
                {
                    JobId = job1Id,
                    ProviderAccountId = accountId,
                    ProfileId = profileId,
                    TabSlotId = "slot-1",
                    CapacityConfigRevision = 1,
                    AssignedAtUtc = nowIso,
                    LeaseOwnerId = "worker-1",
                    LeaseToken = 10
                });

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = accountId,
                    TabSlotId = "slot-1",
                    JobId = job1Id,
                    LeaseOwnerId = "worker-1",
                    LeaseToken = 10,
                    AcquiredAtUtc = nowIso
                });

                await db.SaveChangesAsync();
            }

            // 3. Lease hết hạn -> Quét recovery (PerformRecoverySweepAsync):
            // Job 1 chuyển sang AwaitingUser với reconcile_required. Occupancy của Job 1 vẫn được giữ nguyên.
            var recoveryService = new JobCrashRecoveryService(dbOptions, clock: () => now);
            var sweepResult = await recoveryService.PerformRecoverySweepAsync();

            if (sweepResult.MovedToReconcileCount != 1)
                throw new Exception($"Kỳ vọng 1 job chuyển sang Reconcile, thực tế: {sweepResult.MovedToReconcileCount}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var j1 = await db.Jobs.FirstAsync(j => j.Id == job1Id);
                if (j1.State != JobState.AwaitingUser)
                    throw new Exception($"Kỳ vọng Job 1 State = AwaitingUser, thực tế: {j1.State}");
                if (j1.ReasonCode != JobReasonCode.ReconcileRequired)
                    throw new Exception($"Kỳ vọng Job 1 ReasonCode = reconcile_required, thực tế: {j1.ReasonCode}");

                var occ1 = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == job1Id);
                if (occ1 == null)
                    throw new Exception("Kỳ vọng ActiveSlotOccupancy của Job 1 được giữ nguyên sau recovery sweep.");
            }

            // 4. Người dùng hủy Job 1 (action: "cancel", ConfirmedNotReceived = false/null). Job 1 chuyển Canceled.
            // Do side-effect chưa được giải quyết, ActiveSlotOccupancy của Job 1 VẪN BẮT BUỘC ĐƯỢC GIỮ NGUYÊN!
            var reconcileService = new JobReconciliationService(dbOptions, clock: () => now);
            var cancelRes = await reconcileService.ReconcileAsync(new JobReconcileRequest(
                JobId: job1Id,
                Action: "cancel",
                ConfirmedNotReceived: false
            ));

            if (cancelRes.CurrentState != JobState.Canceled)
                throw new Exception($"Kỳ vọng Job 1 CurrentState = Canceled, thực tế: {cancelRes.CurrentState}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var j1 = await db.Jobs.FirstAsync(j => j.Id == job1Id);
                if (j1.State != JobState.Canceled)
                    throw new Exception($"Kỳ vọng Job 1 trong DB có State = Canceled, thực tế: {j1.State}");
                if (j1.ReasonCode != JobReasonCode.UserCanceled)
                    throw new Exception($"Kỳ vọng Job 1 trong DB có ReasonCode = user_canceled, thực tế: {j1.ReasonCode}");

                var occ1 = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == job1Id);
                if (occ1 == null)
                    throw new Exception("BẢO VỆ CAPACITY: ActiveSlotOccupancy của Job 1 VẪN BẮT BUỘC ĐƯỢC GIỮ NGUYÊN khi cancel unresolved side-effect!");
            }

            // 5. Tạo Job 2 trên cùng provider/account. Thử reserve (TryReserveJobAsync).
            // Khẳng định: reserveResult.Acquired == false (bị chặn vì account capacity = 1 đã bị chiếm bởi occupancy chưa giải quyết của Job 1, cleanup orphan không được xóa occupancy này!).
            var job2Id = Guid.NewGuid().ToString();
            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = job2Id,
                    ProjectId = prjId,
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    ProviderKey = "chatgpt-web",
                    State = JobState.Queued,
                    Attempts = 0,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });
                await db.SaveChangesAsync();
            }

            var reservationService = new JobReservation(dbOptions, () => now);
            var reserveResult1 = await reservationService.TryReserveJobAsync(job2Id, "worker-2", TimeSpan.FromMinutes(5));

            if (reserveResult1.Acquired)
                throw new Exception("Kỳ vọng reserve Job 2 thất bại vì account capacity = 1 đã bị chiếm bởi Job 1 chưa giải quyết side-effect!");

            // Kiểm tra lại occupancy của Job 1 không bị xóa bởi orphan cleanup trong TryReserveJobAsync
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var occ1StillExists = await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == job1Id);
                if (!occ1StillExists)
                    throw new Exception("ActiveSlotOccupancy của Job 1 bị orphan cleanup xóa trái phép trong TryReserveJobAsync!");
            }

            // 6. Thực hiện đối soát an toàn cho Job 1: gọi ReconcileAsync với action: "cancel", ConfirmedNotReceived: true.
            // Khẳng định: ActiveSlotOccupancy của Job 1 bây giờ mới được giải phóng!
            var safeReconcileRes = await reconcileService.ReconcileAsync(new JobReconcileRequest(
                JobId: job1Id,
                Action: "cancel",
                ConfirmedNotReceived: true
            ));

            if (safeReconcileRes.CurrentState != JobState.Canceled)
                throw new Exception($"Kỳ vọng Job 1 sau đối soát an toàn vẫn có CurrentState = Canceled, thực tế: {safeReconcileRes.CurrentState}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var occ1AfterSafeReconcile = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == job1Id);
                if (occ1AfterSafeReconcile != null)
                    throw new Exception("Kỳ vọng ActiveSlotOccupancy của Job 1 ĐƯỢC GIẢI PHÓNG sau khi đối soát an toàn với ConfirmedNotReceived == true.");
            }

            // 7. Thử reserve lại Job 2:
            // Khẳng định: reserveResult.Acquired == true!
            var reserveResult2 = await reservationService.TryReserveJobAsync(job2Id, "worker-2", TimeSpan.FromMinutes(5));
            if (!reserveResult2.Acquired)
                throw new Exception($"Kỳ vọng Job 2 reserve thành công sau khi Job 1 đã được giải phóng slot, nhưng thất bại: {reserveResult2.Reason}");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var occ2 = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == job2Id);
                if (occ2 == null)
                    throw new Exception("Kỳ vọng Job 2 đã chiếm slot occupancy sau khi reserve thành công!");
                if (occ2.ProviderAccountId != accountId)
                    throw new Exception($"Kỳ vọng Job 2 dùng account '{accountId}', thực tế: {occ2.ProviderAccountId}");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 11: Hủy job đã submit side effect kết thúc ngay (không đối soát):
    /// biến mất khỏi list_active, xóa cờ side effect và giải phóng slot cho job kế tiếp.
    /// Kịch bản: submit -> recovery -> cancel -> không còn active và slot rảnh.
    /// </summary>
    public static async Task Test11_CancelAfterSubmit_ReleasesSlotImmediately_NoReconcile()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var expiredLease = now.AddMinutes(-10).ToString("o");

            const string accountId = "account-chatgpt-default";
            const string profileId = "shared_ai";
            var job1Id = Guid.NewGuid().ToString("D");

            // 1. Cấu hình ProviderAccount với MaxConcurrentJobs = 1, MaxTabs = 1
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var account = await db.ProviderAccounts.FirstAsync(a => a.Id == accountId);
                account.MaxConcurrentJobs = 1;
                account.MaxTabs = 1;

                // Tạo Job 1 đang Running và submit side-effect
                db.Jobs.Add(new Job
                {
                    Id = job1Id,
                    ProjectId = prjId,
                    SceneId = scnId,
                    TaskType = JobTaskType.Image,
                    ProviderKey = "chatgpt-web",
                    State = JobState.Running,
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 1,
                    SideEffectAtUtc = now.AddMinutes(-15).ToString("o"),
                    LeaseOwnerId = "worker-1",
                    LeaseExpiresAtUtc = expiredLease,
                    FencingToken = 10,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });

                db.ExecutionBindings.Add(new ExecutionBinding
                {
                    JobId = job1Id,
                    ProviderAccountId = accountId,
                    ProfileId = profileId,
                    TabSlotId = "slot-1",
                    CapacityConfigRevision = 1,
                    AssignedAtUtc = nowIso,
                    LeaseOwnerId = "worker-1",
                    LeaseToken = 10
                });

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = accountId,
                    TabSlotId = "slot-1",
                    JobId = job1Id,
                    LeaseOwnerId = "worker-1",
                    LeaseToken = 10,
                    AcquiredAtUtc = nowIso
                });

                await db.SaveChangesAsync();
            }

            // 2. Lease hết hạn -> Quét recovery đưa sang AwaitingUser (occupancy bảo lưu)
            var recoveryService = new JobCrashRecoveryService(dbOptions, clock: () => now);
            var sweepResult = await recoveryService.PerformRecoverySweepAsync();
            if (sweepResult.MovedToReconcileCount != 1)
            {
                throw new Exception($"Kỳ vọng 1 job chuyển sang Reconcile, thực tế: {sweepResult.MovedToReconcileCount}");
            }

            // 3. Khởi tạo JobService qua production composition
            var reservation = new JobReservation(dbOptions, () => now);
            await using var scheduler = new JobScheduler(dbOptions, reservation, new DeterministicFakeJobExecutor(), clock: () => now);
            var rootRegistry = new StorageRootRegistry(dbOptions);
            var settingsService = new SettingsService(dbOptions, rootRegistry);
            var snapshotFactory = new JobSnapshotFactory(settingsService);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler, clock: () => now);

            // 4. Người dùng gọi JobService.CancelAsync
            var cancelRes = await jobService.CancelAsync(job1Id);
            if (cancelRes.State != JobState.Canceled)
            {
                throw new Exception($"Kỳ vọng CancelAsync trả về Canceled, thực tế: {cancelRes.State}");
            }

            // 5. Hủy là kết thúc ngay: job không còn trong list_active, không còn cờ side effect và slot được giải phóng
            var freshJobService = new JobService(dbOptions, snapshotFactory, scheduler, clock: () => now);
            var activeAfterCancel = await freshJobService.ListActiveAsync();
            if (activeAfterCancel.Jobs.Any(j => j.Id == job1Id))
            {
                throw new Exception("Job đã hủy PHẢI biến mất khỏi list_active, không có bước đối soát!");
            }

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var j1 = await db.Jobs.AsNoTracking().FirstAsync(x => x.Id == job1Id);
                if (j1.SideEffectSubmitted != 0)
                    throw new Exception("Hủy phải xóa cờ side effect để job không chờ đối soát!");
                if (await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == job1Id))
                    throw new Exception("ActiveSlotOccupancy phải được giải phóng ngay khi hủy!");
            }

            // KHẲNG ĐỊNH 4: Capacity=1 rảnh, Job 2 có thể reserve thành công ngay lập tức
            var job2Id = Guid.NewGuid().ToString("D");
            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = job2Id,
                    ProjectId = prjId,
                    SceneId = scnId,
                    TaskType = JobTaskType.Image,
                    ProviderKey = "chatgpt-web",
                    State = JobState.Queued,
                    Attempts = 0,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    CreatedAtUtc = nowIso,
                    UpdatedAtUtc = nowIso
                });
                await db.SaveChangesAsync();
            }

            var reserveRes2 = await reservation.TryReserveJobAsync(job2Id, "worker_2", TimeSpan.FromMinutes(5), cancellationToken: CancellationToken.None);
            if (!reserveRes2.Acquired || reserveRes2.Job?.Id != job2Id)
            {
                throw new Exception("Job 2 phải chiếm được slot ngay sau khi Job 1 được giải phóng!");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    public static async Task Test12_LateResultAfterCancel_StaysCanceled_UnlinksAsset()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            var (prjId, scnId) = await SeedBasicEntitiesAsync(dbOptions);
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var jobId = Guid.NewGuid().ToString("D");
            var assetId = Guid.NewGuid().ToString("N");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = jobId, ProjectId = prjId, SceneId = scnId, TaskType = JobTaskType.Image, ProviderKey = "chatgpt-web",
                    State = JobState.Running, Attempts = 1, MaxAttempts = 3, SideEffectSubmitted = 1,
                    LeaseOwnerId = "worker-1", LeaseExpiresAtUtc = now.AddMinutes(5).ToString("o"), FencingToken = 10,
                    CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso
                });
                db.Assets.Add(new Asset
                {
                    Id = assetId, ProjectId = prjId, AssetType = "Image", StorageRootId = "root_default",
                    RelativePath = $"{prjId}/images/{assetId}.png", ByteSize = 1024, Sha256Hash = "abc123hash",
                    Status = "Linked", CreatedAtUtc = nowIso
                });
                await db.SaveChangesAsync();
            }

            var reservation = new JobReservation(dbOptions, () => now);
            await using var scheduler = new JobScheduler(dbOptions, reservation, new DeterministicFakeJobExecutor(), clock: () => now);
            var settingsService = new SettingsService(dbOptions, new StorageRootRegistry(dbOptions));
            var jobService = new JobService(dbOptions, new JobSnapshotFactory(settingsService), scheduler, clock: () => now);
            await jobService.CancelAsync(jobId);

            // Worker cũ vẫn giữ fencing token 10 và báo thành công muộn.
            var blocked = false;
            try { await reservation.CommitSuccessAsync(jobId, 10, assetId, null); }
            catch (FencingTokenStaleException) { blocked = true; }
            if (!blocked)
                throw new Exception("Kết quả muộn sau khi hủy phải bị chặn.");

            await using (var db = new FrayloDbContext(dbOptions))
            {
                var job = await db.Jobs.AsNoTracking().FirstAsync(x => x.Id == jobId);
                if (job.State != JobState.Canceled || !string.IsNullOrWhiteSpace(job.ResultReference))
                    throw new Exception("Job đã hủy không được nhận kết quả muộn.");
                var asset = await db.Assets.AsNoTracking().FirstAsync(x => x.Id == assetId);
                if (asset.Status != "Unlinked")
                    throw new Exception($"Asset của kết quả muộn phải Unlinked, thực tế: {asset.Status}");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Chạy toàn bộ 12 bài test khói Crash Recovery & Reconcile Engine.
    /// </summary>
    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("     Fraylo — Job Crash Recovery & Reconcile Smoke Checks (T16)  ");
        Console.WriteLine("=================================================================");

        int passed = 0;
        int failed = 0;

        async Task RunCheckAsync(string testName, Func<Task> testAction)
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
                Console.WriteLine($"      -> Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"      -> Inner: {ex.InnerException.Message}");
                }
            }
        }

        await RunCheckAsync("Legacy auto-retry stops; manual and new jobs remain queued",
            Test_LegacyAutoRetry_StopsButManualAndNewJobsRemainQueued);

        await RunCheckAsync(
            "Test 1: Crash before side-effect stops at Failed and deletes occupancy",
            Test_CrashBeforeSideEffect_StopsAndDeletesOccupancy);

        await RunCheckAsync(
            "Test 2: Crash before side-effect with max attempts recovers to Failed and deletes occupancy",
            Test_CrashBeforeSideEffect_MaxAttemptsExceeded_RecoversToFailedAndDeletesOccupancy);

        await RunCheckAsync(
            "Test 3: Crash after side-effect recovers to AwaitingUser and PRESERVES occupancy",
            Test_CrashAfterSideEffect_RecoversToAwaitingUserAndPreservesOccupancy);

        await RunCheckAsync(
            "Test 4: Concurrent recovery sweeps have exactly one winner and no corruption",
            Test_ConcurrentRecoverySweeps_ExactlyOneWinnerNoCorruption);

        await RunCheckAsync(
            "Test 5: Reconcile resubmit requires ConfirmedNotReceived when side-effect submitted",
            Test_ReconcileResubmit_RequiresConfirmedNotReceivedWhenSideEffectSubmitted);

        await RunCheckAsync(
            "Test 6: Reconcile mark_succeeded requires valid result and releases occupancy",
            Test_ReconcileMarkSucceeded_RequiresValidResultAndReleasesOccupancy);

        await RunCheckAsync(
            "Test 7: Reconcile mark_failed and cancel work properly and release occupancy",
            Test_ReconcileMarkFailedAndCancel_WorksProperlyAndReleasesOccupancy);

        await RunCheckAsync(
            "Test 8: Restart startup recovery preserves ExecutionBinding, snapshots, and occupancy",
            Test_RestartStartupRecovery_PreservesBindingAndSnapshots);

        await RunCheckAsync(
            "Test 9: Capacity 1 cancel unresolved side-effect preserves occupancy and blocks new job until safe reconcile",
            Test9_Capacity1_Submit_LeaseExpired_AwaitingUser_Cancel_BlocksNewJobUntilReconciled);

        await RunCheckAsync(
            "Test 11: Cancel after submit finishes immediately, frees slot, no reconcile",
            Test11_CancelAfterSubmit_ReleasesSlotImmediately_NoReconcile);

        await RunCheckAsync(
            "Test 12: Late result after cancel stays Canceled and unlinks the asset",
            Test12_LateResultAfterCancel_StaysCanceled_UnlinksAsset);

        Console.WriteLine("=================================================================");
        if (failed == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"    ALL CRASH RECOVERY SMOKE CHECKS PASSED ({passed}/{passed})       ");
            Console.ResetColor();
            Console.WriteLine("=================================================================");
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"    CRASH RECOVERY SMOKE CHECKS FAILED: {failed} failed, {passed} passed");
            Console.ResetColor();
            Console.WriteLine("=================================================================");
            return 1;
        }
    }
}
