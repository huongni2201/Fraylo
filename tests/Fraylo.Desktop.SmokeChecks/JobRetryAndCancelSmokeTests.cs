using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói định lượng Task T16:
/// Job Retry, Finite Exponential Backoff, Preservation of Attempts on Capacity Exceeded,
/// Preservation of Execution Binding and Snapshots, Safe Cancellation (pre/post-submit),
/// Late Result Harvesting on Canceled Jobs, and Zombie Worker Fencing.
/// </summary>
public static class JobRetryAndCancelSmokeTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] {message}");
        }
    }

    private static (DbContextOptions<FrayloDbContext> Options, string TempDir) CreateTestContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"titoai_t16_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "titoai_test.db");
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

            if (!db.BrowserProfiles.Any(p => p.Id == "test_profile"))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "test_profile",
                    Kind = "SharedAI",
                    ProviderKey = "gemini-web",
                    DisplayName = "Test Google Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }

            if (!db.ProviderAccounts.Any(a => a.Id == "test_account"))
            {
                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "test_account",
                    ProviderKey = "gemini-web",
                    ProfileId = "test_profile",
                    DisplayName = "Gemini Account 1",
                    VerifiedIdentity = "user@test.local",
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
            if (Directory.Exists(tempDir))
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Bỏ qua lỗi xóa thư mục tạm trong môi trường Windows CI/test
        }
    }

    private static async Task<string> SeedProjectAsync(FrayloDbContext db)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        var prjId = Guid.NewGuid().ToString();


        var prj = new Project
        {
            Id = prjId,
            Name = "Project Smoke Test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Projects.Add(prj);

        await db.SaveChangesAsync().ConfigureAwait(false);
        return prjId;
    }

    /// <summary>
    /// Test 1: Retry đạt giới hạn MaxAttempts chuyển sang Failed với reason max_attempts_exceeded hoặc lease_expired_max_attempts.
    /// </summary>
    public static async Task Test1_Retry_ReachesMaxAttempts_TransitionsToFailed()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            var prjId = await SeedProjectAsync(db);
            var now = DateTimeOffset.UtcNow.ToString("o");

            var job = new Job
            {
                Id = Guid.NewGuid().ToString(),
                ProjectId = prjId,
                TaskType = JobTaskType.Script,
                State = JobState.Running,
                Attempts = 3,
                MaxAttempts = 3,
                SideEffectSubmitted = 0,
                StorageRootId = "root_test",
                ProviderKey = "gemini-web",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();

            // 1. Kiểm tra CanRetry: Khi Attempts >= MaxAttempts => false
            bool canRetry = JobRetryAndCancelPolicy.CanRetry(job, JobReasonCode.NetworkError, sideEffectSubmitted: false);
            Assert(!canRetry, "Job đạt MaxAttempts (3/3) không được phép retry tự động.");

            // 2. Kiểm tra EvaluateFailure với NetworkError
            var decision = JobRetryAndCancelPolicy.EvaluateFailure(job, JobReasonCode.NetworkError, sideEffectSubmitted: false);
            Assert(!decision.CanRetry, "Decision CanRetry phải là false.");
            Assert(decision.TargetState == JobState.Failed, $"TargetState phải là 'Failed', thực tế: '{decision.TargetState}'.");
            Assert(decision.FinalReasonCode == JobReasonCode.MaxAttemptsExceeded,
                $"FinalReasonCode phải là 'max_attempts_exceeded', thực tế: '{decision.FinalReasonCode}'.");
            Assert(decision.Classification == FailureClassification.PermanentFailure,
                "Classification phải là PermanentFailure.");

            // 3. Kiểm tra EvaluateFailure với LeaseExpiredRetry
            var leaseDecision = JobRetryAndCancelPolicy.EvaluateFailure(job, JobReasonCode.LeaseExpiredRetry, sideEffectSubmitted: false);
            Assert(leaseDecision.FinalReasonCode == JobReasonCode.LeaseExpiredMaxAttempts,
                $"FinalReasonCode khi lease hết hạn đạt max attempts phải là 'lease_expired_max_attempts', thực tế: '{leaseDecision.FinalReasonCode}'.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 2: Job lỗi dừng ngay dù vẫn còn ngân sách thử lại.
    /// </summary>
    public static void Test2_Failure_DoesNotAutoRetry()
    {
        foreach (var taskType in JobTaskType.All)
        {
            var job = new Job { TaskType = taskType, Attempts = 1, MaxAttempts = 3 };
            foreach (var reason in new[] { JobReasonCode.NetworkError, "execution_failed", JobReasonCode.ProviderRejected })
            {
                var decision = JobRetryAndCancelPolicy.EvaluateFailure(job, reason, false);
                Assert(!decision.CanRetry && decision.TargetState == JobState.Failed && decision.RetryDelay == null,
                    $"{taskType}/{reason}: job lỗi không được tự retry.");
                Assert(decision.FinalReasonCode == reason, "Phải giữ mã lỗi gốc.");
                Assert(!JobRetryAndCancelPolicy.CanRetry(job, reason, false), "Helper không được cho phép tự retry.");
            }
        }
    }

    /// <summary>
    /// Test 3: Pool đầy (tab_capacity_unavailable, waiting_for_capacity, global_job_budget_reached) không tiêu hao attempts.
    /// </summary>
    public static async Task Test3_PoolFull_TabCapacityUnavailable_PreservesAttempts()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            var prjId = await SeedProjectAsync(db);
            var now = DateTimeOffset.UtcNow.ToString("o");

            var job = new Job
            {
                Id = Guid.NewGuid().ToString(),
                ProjectId = prjId,
                TaskType = JobTaskType.Script,
                State = JobState.Queued,
                Attempts = 1,
                MaxAttempts = 3,
                SideEffectSubmitted = 0,
                StorageRootId = "root_test",
                ProviderKey = "gemini-web",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();

            string[] capacityReasons =
            [
                JobReasonCode.TabCapacityUnavailable,
                JobReasonCode.WaitingForCapacity,
                JobReasonCode.GlobalJobBudgetReached
            ];

            foreach (var reason in capacityReasons)
            {
                // Kiểm tra helper ShouldRollbackAttemptsOnCapacityExceeded
                bool shouldRollback = JobRetryAndCancelPolicy.ShouldRollbackAttemptsOnCapacityExceeded(reason);
                Assert(shouldRollback, $"Lý do '{reason}' phải được xác định là CapacityExceeded để bảo toàn attempts.");

                // Đánh giá quyết định thất bại
                var decision = JobRetryAndCancelPolicy.EvaluateFailure(job, reason, sideEffectSubmitted: false);
                Assert(decision.TargetState == JobState.Queued, $"Lý do '{reason}' phải giữ job ở trạng thái 'Queued'.");
                Assert(decision.ShouldRollbackAttempts, $"Lý do '{reason}' phải có cờ ShouldRollbackAttempts = true.");
                Assert(decision.Classification == FailureClassification.CapacityExceeded, "Classification phải là CapacityExceeded.");
            }

            // Xác minh lại trong DB: attempts của Job vẫn nguyên vẹn là 1
            var reloadedJob = await db.Jobs.FindAsync(job.Id);
            Assert(reloadedJob!.Attempts == 1, $"Attempts của Job trong DB phải được bảo toàn là 1, thực tế: {reloadedJob.Attempts}.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 4: Cancel trước submit (SideEffectSubmitted == 0) chuyển ngay sang Canceled và xóa ActiveSlotOccupancy.
    /// </summary>
    public static async Task Test4_CancelBeforeSubmit_TransitionsToCanceled_AndRemovesOccupancy()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            var prjId = await SeedProjectAsync(db);
            var now = DateTimeOffset.UtcNow.ToString("o");
            var jobId = Guid.NewGuid().ToString();

            var job = new Job
            {
                Id = jobId,
                ProjectId = prjId,
                TaskType = JobTaskType.Script,
                State = JobState.Running,
                Attempts = 1,
                MaxAttempts = 3,
                SideEffectSubmitted = 0,
                LeaseOwnerId = "worker_alpha",
                FencingToken = 10,
                StorageRootId = "root_test",
                ProviderKey = "gemini-web",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Jobs.Add(job);

            // Thêm ActiveSlotOccupancy mô phỏng tab đang bị chiếm
            var occupancy = new ActiveSlotOccupancy
            {
                ProviderAccountId = "test_account",
                TabSlotId = "slot_1",
                JobId = jobId,
                LeaseOwnerId = "worker_alpha",
                LeaseToken = 10,
                AcquiredAtUtc = now
            };
            db.ActiveSlotOccupancies.Add(occupancy);
            await db.SaveChangesAsync();

            // Thực hiện hủy tác vụ trước submit
            var cancelResult = await JobRetryAndCancelPolicy.HandleCancellationRequestAsync(db, jobId);

            Assert(cancelResult.FinalState == JobState.Canceled, $"Trạng thái trả về phải là Canceled, thực tế: '{cancelResult.FinalState}'.");
            Assert(!cancelResult.CancelRequested, "CancelRequested phải là false (vì chuyển Canceled ngay lập tức).");
            Assert(cancelResult.OccupancyReleased, "OccupancyReleased phải là true.");

            // Kiểm tra trạng thái trong DB
            var dbJob = await db.Jobs.FindAsync(jobId);
            Assert(dbJob!.State == JobState.Canceled, $"State trong DB phải là Canceled, thực tế: '{dbJob.State}'.");
            Assert(dbJob.ReasonCode == JobReasonCode.UserCanceled, $"ReasonCode trong DB phải là 'user_canceled', thực tế: '{dbJob.ReasonCode}'.");
            Assert(dbJob.LeaseOwnerId == null, "LeaseOwnerId phải được xóa về null.");
            Assert(dbJob.LeaseExpiresAtUtc == null, "LeaseExpiresAtUtc phải được xóa về null.");

            // Kiểm tra slot occupancy đã bị xóa hoàn toàn khỏi DB
            var dbOccupancy = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == jobId);
            Assert(dbOccupancy == null, "ActiveSlotOccupancy phải bị xóa hoàn toàn khỏi DB khi hủy trước submit.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 5: Cancel sau submit (SideEffectSubmitted == 1):
    /// - Yêu cầu cancel: job nhận CancelRequested = 1, giữ nguyên slot occupancy để tránh mất ngữ cảnh.
    /// - Late Result đến: lưu trữ ResultReference, trạng thái cuối cùng PHẢI LÀ Canceled, KHÔNG ĐƯỢC THÀNH Succeeded.
    /// - Giải phóng slot occupancy an toàn.
    /// </summary>
    public static async Task Test5_CancelAfterSubmit_SetsCancelRequested_LateResultStoresReferenceAndRemainsCanceled()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            var prjId = await SeedProjectAsync(db);
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var futureIso = now.AddMinutes(5).ToString("o");
            var jobId = Guid.NewGuid().ToString();
            long validFencingToken = 42;

            var job = new Job
            {
                Id = jobId,
                ProjectId = prjId,
                TaskType = JobTaskType.Script,
                State = JobState.Running,
                Attempts = 1,
                MaxAttempts = 3,
                SideEffectSubmitted = 1, // Đã submit ra ngoài
                SideEffectAtUtc = nowIso,
                LeaseOwnerId = "worker_bravo",
                FencingToken = validFencingToken,
                LeaseExpiresAtUtc = futureIso,
                StorageRootId = "root_test",
                ProviderKey = "gemini-web",
                CreatedAtUtc = nowIso,
                UpdatedAtUtc = nowIso
            };
            db.Jobs.Add(job);

            var occupancy = new ActiveSlotOccupancy
            {
                ProviderAccountId = "test_account",
                TabSlotId = "slot_2",
                JobId = jobId,
                LeaseOwnerId = "worker_bravo",
                LeaseToken = validFencingToken,
                AcquiredAtUtc = nowIso
            };
            db.ActiveSlotOccupancies.Add(occupancy);

            // Tạo sẵn một Asset trong DB để kiểm tra đánh dấu Unlinked
            var assetId = "asset_late_001";
            var asset = new Asset
            {
                Id = assetId,
                ProjectId = prjId,
                StorageRootId = "root_test",
                RelativePath = "scripts/late_result.txt",
                AssetType = "Document",
                Status = "Linked",
                CreatedAtUtc = nowIso
            };
            db.Assets.Add(asset);

            await db.SaveChangesAsync();

            // BƯỚC 1: Người dùng yêu cầu hủy sau submit
            var cancelResult = await JobRetryAndCancelPolicy.HandleCancellationRequestAsync(db, jobId);

            Assert(cancelResult.CancelRequested, "CancelRequested trả về phải là true.");
            Assert(!cancelResult.OccupancyReleased, "Occupancy KHÔNG được giải phóng ngay sau submit để giữ ngữ cảnh.");

            var intermediateJob = await db.Jobs.FindAsync(jobId);
            Assert(intermediateJob!.CancelRequested == 1, "Cờ CancelRequested trong DB phải được bật thành 1.");
            Assert(intermediateJob.State == JobState.Running, "State trong DB phải giữ nguyên Running để worker kết thúc an toàn.");

            var intermediateOccupancy = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == jobId);
            Assert(intermediateOccupancy != null, "ActiveSlotOccupancy VẪN PHẢI TỒN TẠI trong DB khi đang chờ late result.");

            // BƯỚC 2: Worker hoàn tất và trả về kết quả đến muộn (Late Result)
            string returnedRef = assetId;
            string returnedText = "Late generated video script result.";

            var lateHandlingResult = await JobRetryAndCancelPolicy.HandleLateResultOnCanceledJobAsync(
                db,
                jobId,
                validFencingToken,
                returnedRef,
                returnedText
            );

            Assert(lateHandlingResult.FinalState == JobState.Canceled,
                $"Late result handling phải trả về trạng thái Canceled, thực tế: '{lateHandlingResult.FinalState}'.");
            Assert(lateHandlingResult.OccupancyReleased, "OccupancyReleased phải là true sau khi xử lý late result.");
            Assert(lateHandlingResult.ResultReference == returnedRef, "ResultReference phải được thu hồi.");

            // BƯỚC 3: Xác minh dữ liệu trong DB
            var finalJob = await db.Jobs.FindAsync(jobId);
            Assert(finalJob!.State == JobState.Canceled,
                $"Trạng thái Job trong DB TUYỆT ĐỐI PHẢI LÀ Canceled, không được thành Succeeded! Thực tế: '{finalJob.State}'.");
            Assert(finalJob.State != JobState.Succeeded, "TUYỆT ĐỐI CẤM trạng thái Succeeded khi job đã bị hủy!");
            Assert(finalJob.ResultReference == returnedRef, "ResultReference trong DB phải được lưu trữ an toàn.");
            Assert(finalJob.ResultText == returnedText, "ResultText trong DB phải được lưu trữ an toàn.");
            Assert(finalJob.ReasonCode == JobReasonCode.CanceledLateResult,
                $"ReasonCode trong DB phải là 'canceled_late_result', thực tế: '{finalJob.ReasonCode}'.");

            // Xác minh Asset được chuyển thành "Unlinked" để bảo vệ an toàn file vật lý
            var finalAsset = await db.Assets.FindAsync(assetId);
            Assert(finalAsset!.Status == "Unlinked", $"Asset status phải chuyển thành 'Unlinked', thực tế: '{finalAsset.Status}'.");

            // Xác minh ActiveSlotOccupancy đã được giải phóng an toàn
            var finalOccupancy = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == jobId);
            Assert(finalOccupancy == null, "ActiveSlotOccupancy phải được giải phóng hoàn toàn sau khi xử lý late result.");

            // BƯỚC 4 (T16 FINDING 2): Kiểm tra Job ở trạng thái AwaitingUser có unresolved side effect (SideEffectSubmitted == 1, chưa có kết quả):
            // Khi hủy: chuyển sang Canceled nhưng TUYỆT ĐỐI KHÔNG giải phóng ActiveSlotOccupancy!
            // Khi late result đến: lưu kết quả, chuyển Asset sang Unlinked và GIẢI PHÓNG ActiveSlotOccupancy.
            var awaitingJobId = Guid.NewGuid().ToString();
            long awaitingFencingToken = 99;
            var awaitingJob = new Job
            {
                Id = awaitingJobId,
                ProjectId = prjId,
                TaskType = JobTaskType.Script,
                State = JobState.AwaitingUser,
                Attempts = 1,
                MaxAttempts = 3,
                SideEffectSubmitted = 1, // Đã submit nhưng chưa có kết quả (unresolved side effect)
                SideEffectAtUtc = nowIso,
                LeaseOwnerId = "worker_awaiting",
                FencingToken = awaitingFencingToken,
                StorageRootId = "root_test",
                ProviderKey = "gemini-web",
                CreatedAtUtc = nowIso,
                UpdatedAtUtc = nowIso
            };
            db.Jobs.Add(awaitingJob);

            var awaitingOccupancy = new ActiveSlotOccupancy
            {
                ProviderAccountId = "test_account",
                TabSlotId = "slot_3",
                JobId = awaitingJobId,
                LeaseOwnerId = "worker_awaiting",
                LeaseToken = awaitingFencingToken,
                AcquiredAtUtc = nowIso
            };
            db.ActiveSlotOccupancies.Add(awaitingOccupancy);

            var awaitingAssetId = "asset_awaiting_001";
            var awaitingAsset = new Asset
            {
                Id = awaitingAssetId,
                ProjectId = prjId,
                StorageRootId = "root_test",
                RelativePath = "scripts/awaiting_result.txt",
                AssetType = "Document",
                Status = "Linked",
                CreatedAtUtc = nowIso
            };
            db.Assets.Add(awaitingAsset);
            await db.SaveChangesAsync();

            // Người dùng hủy tác vụ AwaitingUser có unresolved side effect
            var cancelAwaitingResult = await JobRetryAndCancelPolicy.HandleCancellationRequestAsync(db, awaitingJobId);
            Assert(cancelAwaitingResult.FinalState == JobState.Canceled,
                $"AwaitingUser cancel phải chuyển sang Canceled, thực tế: '{cancelAwaitingResult.FinalState}'.");
            Assert(!cancelAwaitingResult.CancelRequested, "CancelRequested phải là false vì đã chuyển Canceled ngay.");
            Assert(!cancelAwaitingResult.OccupancyReleased,
                "T16 FINDING 2: OccupancyReleased TUYỆT ĐỐI PHẢI LÀ FALSE khi hủy job AwaitingUser có unresolved side effect!");

            // Kiểm tra DB: Occupancy vẫn tồn tại nguyên vẹn
            var persistedAwaitingOccupancy = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == awaitingJobId);
            Assert(persistedAwaitingOccupancy != null,
                "T16 FINDING 2: ActiveSlotOccupancy VẪN PHẢI TỒN TẠI trong DB khi hủy job AwaitingUser có unresolved side effect!");

            // Late result đến trên tác vụ AwaitingUser đã hủy: giải phóng occupancy an toàn
            var lateAwaitingHandlingResult = await JobRetryAndCancelPolicy.HandleLateResultOnCanceledJobAsync(
                db,
                awaitingJobId,
                awaitingFencingToken,
                awaitingAssetId,
                "Late result for awaiting job"
            );

            Assert(lateAwaitingHandlingResult.FinalState == JobState.Canceled, "Late result phải giữ trạng thái Canceled.");
            Assert(lateAwaitingHandlingResult.OccupancyReleased, "OccupancyReleased phải là true sau khi xử lý late result.");

            // Kiểm tra DB sau late result: Occupancy đã được giải phóng và Asset thành Unlinked
            var releasedAwaitingOccupancy = await db.ActiveSlotOccupancies.FirstOrDefaultAsync(o => o.JobId == awaitingJobId);
            Assert(releasedAwaitingOccupancy == null, "ActiveSlotOccupancy phải được giải phóng hoàn toàn sau khi nộp late result!");

            var unlinkedAsset = await db.Assets.FindAsync(awaitingAssetId);
            Assert(unlinkedAsset!.Status == "Unlinked", $"Asset status phải là 'Unlinked', thực tế: '{unlinkedAsset.Status}'.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 6: Zombie Worker (fencing token không khớp hoặc lease expired hoặc job đã Failed/AwaitingUser)
    /// cố commit kết quả bị chặn bằng FencingTokenStaleException và không được phép ghi đè DB.
    /// </summary>
    public static async Task Test6_ZombieWorker_CommitAfterCancelOrExpiredLease_BlockedByFencingTokenStaleException()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            var prjId = await SeedProjectAsync(db);
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var pastIso = now.AddMinutes(-5).ToString("o"); // Lease đã hết hạn 5 phút trước
            var jobId = Guid.NewGuid().ToString();

            var job = new Job
            {
                Id = jobId,
                ProjectId = prjId,
                TaskType = JobTaskType.Script,
                State = JobState.Canceled,
                ReasonCode = JobReasonCode.UserCanceled,
                Attempts = 1,
                MaxAttempts = 3,
                SideEffectSubmitted = 1,
                CancelRequested = 1,
                LeaseOwnerId = "zombie_worker",
                FencingToken = 20, // DB token hiện tại là 20
                LeaseExpiresAtUtc = pastIso, // Đã hết hạn
                StorageRootId = "root_test",
                ProviderKey = "gemini-web",
                CreatedAtUtc = nowIso,
                UpdatedAtUtc = nowIso
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();

            // TRƯỜNG HỢP 1: Zombie Worker dùng token cũ (stale token = 19 != 20)
            bool staleTokenExceptionThrown = false;
            try
            {
                await JobRetryAndCancelPolicy.HandleLateResultOnCanceledJobAsync(
                    db,
                    jobId,
                    fencingToken: 19, // Token cũ không khớp
                    resultReference: "stale_result_ref",
                    resultText: "Stale result text"
                );
            }
            catch (FencingTokenStaleException ex)
            {
                staleTokenExceptionThrown = true;
                Assert(ex.JobId == jobId, "FencingTokenStaleException phải chứa đúng jobId.");
                Assert(ex.ExpectedToken == 19, "FencingTokenStaleException phải chứa đúng expectedToken.");
            }
            Assert(staleTokenExceptionThrown, "Zombie Worker với stale fencing token bắt buộc phải ném FencingTokenStaleException.");

            // TRƯỜNG HỢP 2: Zombie Worker dùng token 20 nhưng Lease đã hết hạn trong quá khứ
            bool expiredLeaseExceptionThrown = false;
            try
            {
                await JobRetryAndCancelPolicy.HandleLateResultOnCanceledJobAsync(
                    db,
                    jobId,
                    fencingToken: 20, // Khớp token nhưng lease đã hết hạn
                    resultReference: "expired_lease_ref",
                    resultText: "Expired lease text"
                );
            }
            catch (FencingTokenStaleException)
            {
                expiredLeaseExceptionThrown = true;
            }
            Assert(expiredLeaseExceptionThrown, "Zombie Worker với lease đã hết hạn bắt buộc phải ném FencingTokenStaleException.");

            // TRƯỜNG HỢP 3: Job đã bị chuyển sang Failed bởi crash recovery sweep, worker cũ cố commit
            job.State = JobState.Failed;
            job.ReasonCode = JobReasonCode.MaxAttemptsExceeded;
            job.LeaseExpiresAtUtc = null;
            await db.SaveChangesAsync();

            bool failedStateOverwriteBlocked = false;
            try
            {
                await JobRetryAndCancelPolicy.HandleLateResultOnCanceledJobAsync(
                    db,
                    jobId,
                    fencingToken: 20,
                    resultReference: "failed_overwrite_ref",
                    resultText: "Should never overwrite Failed"
                );
            }
            catch (FencingTokenStaleException)
            {
                failedStateOverwriteBlocked = true;
            }
            Assert(failedStateOverwriteBlocked, "Zombie Worker cố commit lên Job ở trạng thái Failed phải bị chặn bằng FencingTokenStaleException.");

            // Xác minh lại trong DB: không có bất kỳ kết quả rác nào bị ghi đè
            var verifiedJob = await db.Jobs.FindAsync(jobId);
            Assert(verifiedJob!.State == JobState.Failed, "Job State trong DB phải được bảo vệ nguyên vẹn là Failed.");
            Assert(verifiedJob.ResultReference == null, "ResultReference trong DB không được bị ghi đè.");
            Assert(verifiedJob.ResultText == null, "ResultText trong DB không được bị ghi đè.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 7: Khi job retry hoặc quay về Queued, ExecutionBinding và snapshot input/config/root PHẢI ĐƯỢC GIỮ NGUYÊN.
    /// </summary>
    public static async Task Test7_RetryPreservesExecutionBindingAndSnapshots()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            var prjId = await SeedProjectAsync(db);
            var now = DateTimeOffset.UtcNow.ToString("o");
            var jobId = Guid.NewGuid().ToString();

            const string expectedProviderKey = "gemini-web";
            const string expectedStorageRoot = "root_vault_01";
            const string expectedPromptSnapshot = "Write a comprehensive video script on quantum physics.";
            const string expectedConfigSnapshot = "{\"temperature\":0.7,\"model\":\"gemini-2.5-pro\"}";
            const string expectedAccountId = "test_account";
            const string expectedProfileId = "test_profile";
            const string expectedTabSlot = "slot_alpha";

            var job = new Job
            {
                Id = jobId,
                ProjectId = prjId,
                TaskType = JobTaskType.Script,
                State = JobState.Running,
                Attempts = 1,
                MaxAttempts = 3,
                SideEffectSubmitted = 0,
                StorageRootId = expectedStorageRoot,
                ProviderKey = expectedProviderKey,
                PromptSnapshotText = expectedPromptSnapshot,
                ConfigSnapshotJson = expectedConfigSnapshot,
                LeaseOwnerId = "worker_gamma",
                FencingToken = 33,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Jobs.Add(job);

            var binding = new ExecutionBinding
            {
                JobId = jobId,
                ProviderAccountId = expectedAccountId,
                ProfileId = expectedProfileId,
                TabSlotId = expectedTabSlot,
                AssignedAtUtc = now,
                CapacityConfigRevision = 1
            };
            db.ExecutionBindings.Add(binding);
            await db.SaveChangesAsync();

            // Áp dụng chính sách Retry do lỗi mạng tạm thời
            var retryDelay = JobRetryAndCancelPolicy.CalculateBackoff(job.Attempts);
            JobRetryAndCancelPolicy.ApplyRetryToJob(job, retryDelay, JobReasonCode.NetworkError);

            await db.SaveChangesAsync();

            // Nạp lại dữ liệu từ DB để xác minh tính bất biến
            var reloadedJob = await db.Jobs
                .Include(j => j.ExecutionBinding)
                .FirstOrDefaultAsync(j => j.Id == jobId);

            Assert(reloadedJob != null, "Job phải tồn tại trong DB.");
            Assert(reloadedJob!.State == JobState.Queued, $"Job State phải chuyển về 'Queued', thực tế: '{reloadedJob.State}'.");
            Assert(reloadedJob.ReasonCode == JobReasonCode.NetworkError, $"ReasonCode phải là 'network_error', thực tế: '{reloadedJob.ReasonCode}'.");
            Assert(reloadedJob.LeaseOwnerId == null, "LeaseOwnerId phải được giải phóng về null.");
            Assert(reloadedJob.LeaseExpiresAtUtc == null, "LeaseExpiresAtUtc phải được giải phóng về null.");
            Assert(!string.IsNullOrWhiteSpace(reloadedJob.RetryAfterUtc), "RetryAfterUtc phải được thiết lập thời gian trong tương lai.");

            // BẢO TOÀN SNAPSHOTS: Tuyệt đối không thay đổi
            Assert(reloadedJob.ProviderKey == expectedProviderKey, "ProviderKey snapshot phải được bảo toàn bất biến.");
            Assert(reloadedJob.StorageRootId == expectedStorageRoot, "StorageRootId snapshot phải được bảo toàn bất biến.");
            Assert(reloadedJob.PromptSnapshotText == expectedPromptSnapshot, "PromptSnapshotText phải được bảo toàn bất biến.");
            Assert(reloadedJob.ConfigSnapshotJson == expectedConfigSnapshot, "ConfigSnapshotJson phải được bảo toàn bất biến.");

            // BẢO TOÀN EXECUTION BINDING: Không được đổi account hay profile khác
            Assert(reloadedJob.ExecutionBinding != null, "ExecutionBinding của Job phải được bảo toàn.");
            Assert(reloadedJob.ExecutionBinding!.ProviderAccountId == expectedAccountId,
                $"ProviderAccountId trong ExecutionBinding phải được giữ nguyên là '{expectedAccountId}'.");
            Assert(reloadedJob.ExecutionBinding.ProfileId == expectedProfileId,
                $"ProfileId trong ExecutionBinding phải được giữ nguyên là '{expectedProfileId}'.");
            Assert(reloadedJob.ExecutionBinding.TabSlotId == expectedTabSlot,
                $"TabSlotId trong ExecutionBinding phải được giữ nguyên là '{expectedTabSlot}'.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 8: Ép interleaving giữa SELECT và UPDATE:
    /// - Worker 1 đọc job ở trạng thái Running với fencing_token = 10.
    /// - Recovery sweep hoặc re-reserve can thiệp: tăng fencing_token lên 11 và cấp slot cho Worker 2.
    /// - Worker 1 gọi CommitSuccessAsync với fencingToken = 10.
    /// - Khẳng định Worker 1 bị ném FencingTokenStaleException (do CAS tại UPDATE rows affected == 0).
    /// - Khẳng định token của Worker 2 (11), state (Running), và ActiveSlotOccupancy của Worker 2 được bảo toàn nguyên vẹn 100%, không bị ghi đè!
    /// </summary>
    public static async Task Test8_InterleavedWorker_ReadBeforeRecovery_CommitBlockedByCasAndFencingToken()
    {
        var (dbOptions, tempDir) = CreateTestContext();
        try
        {
            await using var db = new FrayloDbContext(dbOptions);
            var prjId = await SeedProjectAsync(db);
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("o");
            var futureIso = now.AddMinutes(5).ToString("o");
            var jobId = Guid.NewGuid().ToString();

            const long worker1Token = 10;
            const long worker2Token = 11;
            const string worker1Id = "worker_1_interleaved";
            const string worker2Id = "worker_2_winner";
            const string accountId = "test_account";
            const string slotId = "slot_1";

            // BƯỚC 1: Khởi tạo Job ở trạng thái Running thuộc về Worker 1 (FencingToken = 10)
            var job = new Job
            {
                Id = jobId,
                ProjectId = prjId,
                TaskType = JobTaskType.Script,
                State = JobState.Running,
                Attempts = 1,
                MaxAttempts = 3,
                SideEffectSubmitted = 1,
                SideEffectAtUtc = nowIso,
                LeaseOwnerId = worker1Id,
                FencingToken = worker1Token,
                LeaseExpiresAtUtc = futureIso,
                StorageRootId = "root_test",
                ProviderKey = "gemini-web",
                CreatedAtUtc = nowIso,
                UpdatedAtUtc = nowIso
            };
            db.Jobs.Add(job);

            // ActiveSlotOccupancy đang được cấp cho Worker 1
            var occupancy = new ActiveSlotOccupancy
            {
                ProviderAccountId = accountId,
                TabSlotId = slotId,
                JobId = jobId,
                LeaseOwnerId = worker1Id,
                LeaseToken = worker1Token,
                AcquiredAtUtc = nowIso
            };
            db.ActiveSlotOccupancies.Add(occupancy);
            await db.SaveChangesAsync();

            // BƯỚC 2: Mô phỏng Worker 1 đọc job từ DB và giữ fencingToken = 10
            var worker1ObservedJob = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            Assert(worker1ObservedJob != null, "Worker 1 phải đọc được Job từ DB.");
            Assert(worker1ObservedJob!.FencingToken == worker1Token, $"Worker 1 quan sát fencingToken = {worker1Token}.");

            // BƯỚC 3: Can thiệp Interleaving:
            // Recovery sweep hoặc re-reserve can thiệp giữa SELECT và UPDATE:
            // Tăng fencing_token lên 11 và cấp lại lease / slot cho Worker 2
            var recoveryNowIso = DateTimeOffset.UtcNow.ToString("o");
            var recoveryFutureIso = DateTimeOffset.UtcNow.AddMinutes(10).ToString("o");

            job.FencingToken = worker2Token;
            job.LeaseOwnerId = worker2Id;
            job.LeaseExpiresAtUtc = recoveryFutureIso;
            job.UpdatedAtUtc = recoveryNowIso;

            occupancy.LeaseOwnerId = worker2Id;
            occupancy.LeaseToken = worker2Token;
            occupancy.AcquiredAtUtc = recoveryNowIso;

            await db.SaveChangesAsync();

            // BƯỚC 4: Worker 1 gọi CommitSuccessAsync với fencingToken = 10 (stale token)
            var reservation = new JobReservation(dbOptions);
            bool fencingExceptionThrown = false;
            try
            {
                await reservation.CommitSuccessAsync(
                    jobId,
                    worker1Token,
                    resultReference: "stale_worker1_asset_id",
                    resultText: "Stale result from Worker 1"
                );
            }
            catch (FencingTokenStaleException ex)
            {
                fencingExceptionThrown = true;
                Assert(ex.JobId == jobId, $"FencingTokenStaleException phải chứa đúng jobId '{jobId}', thực tế: '{ex.JobId}'.");
                Assert(ex.ExpectedToken == worker1Token, $"FencingTokenStaleException phải chứa expectedToken '{worker1Token}', thực tế: '{ex.ExpectedToken}'.");
            }

            Assert(fencingExceptionThrown, "Worker 1 có stale fencing_token (10 != 11) bắt buộc phải bị ném FencingTokenStaleException do CAS UPDATE rows affected == 0.");

            // BƯỚC 5: Khẳng định Worker 2's token (11), state, và ActiveSlotOccupancy của Worker 2 được bảo toàn nguyên vẹn 100%, không bị ghi đè!
            await using var verifyDb = new FrayloDbContext(dbOptions);
            var reloadedJob = await verifyDb.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            Assert(reloadedJob != null, "Job phải tồn tại trong DB.");
            Assert(reloadedJob!.FencingToken == worker2Token,
                $"FencingToken trong DB phải được bảo toàn là {worker2Token} của Worker 2, thực tế: {reloadedJob.FencingToken}.");
            Assert(reloadedJob.LeaseOwnerId == worker2Id,
                $"LeaseOwnerId trong DB phải thuộc về Worker 2 ('{worker2Id}'), thực tế: '{reloadedJob.LeaseOwnerId}'.");
            Assert(reloadedJob.State == JobState.Running,
                $"Trạng thái Job trong DB phải giữ nguyên 'Running', không bị Worker 1 chuyển thành Succeeded! Thực tế: '{reloadedJob.State}'.");
            Assert(reloadedJob.ResultReference == null,
                $"ResultReference trong DB không được bị Worker 1 ghi đè, thực tế: '{reloadedJob.ResultReference}'.");
            Assert(reloadedJob.ResultText == null,
                $"ResultText trong DB không được bị Worker 1 ghi đè, thực tế: '{reloadedJob.ResultText}'.");

            var reloadedOccupancy = await verifyDb.ActiveSlotOccupancies.AsNoTracking().FirstOrDefaultAsync(o => o.JobId == jobId);
            Assert(reloadedOccupancy != null,
                "ActiveSlotOccupancy của Worker 2 phải được bảo toàn nguyên vẹn 100% trong DB, không bị Worker 1 giải phóng!");
            Assert(reloadedOccupancy!.LeaseOwnerId == worker2Id,
                $"ActiveSlotOccupancy LeaseOwnerId phải là Worker 2 ('{worker2Id}'), thực tế: '{reloadedOccupancy.LeaseOwnerId}'.");
            Assert(reloadedOccupancy.LeaseToken == worker2Token,
                $"ActiveSlotOccupancy LeaseToken phải là {worker2Token}, thực tế: {reloadedOccupancy.LeaseToken}.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Phương thức điều phối chạy toàn bộ 8 ca kiểm thử định lượng Task T16.
    /// </summary>
    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("  Fraylo — Job Retry, Backoff & Cancellation Smoke Checks (T16)  ");
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
            }
        }

        void RunSyncCheck(string testName, Action testAction)
        {
            Console.Write($"[RUN] {testName} ... ");
            try
            {
                testAction();
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
            }
        }

        await RunCheckAsync("Test 1: Retry reaches MaxAttempts transitions to Failed with max_attempts_exceeded",
            Test1_Retry_ReachesMaxAttempts_TransitionsToFailed);

        RunSyncCheck("Test 2: Failed jobs never retry automatically",
            Test2_Failure_DoesNotAutoRetry);

        await RunCheckAsync("Test 3: Pool capacity full preserves attempts without consumption",
            Test3_PoolFull_TabCapacityUnavailable_PreservesAttempts);

        await RunCheckAsync("Test 4: Cancel before submit transitions to Canceled and removes active slot occupancy",
            Test4_CancelBeforeSubmit_TransitionsToCanceled_AndRemovesOccupancy);

        await RunCheckAsync("Test 5: Cancel after submit marks CancelRequested, late result stores reference and remains Canceled",
            Test5_CancelAfterSubmit_SetsCancelRequested_LateResultStoresReferenceAndRemainsCanceled);

        await RunCheckAsync("Test 6: Zombie worker commit after cancel or expired lease blocked by FencingTokenStaleException",
            Test6_ZombieWorker_CommitAfterCancelOrExpiredLease_BlockedByFencingTokenStaleException);

        await RunCheckAsync("Test 7: Retry preserves ExecutionBinding and immutable snapshots intact",
            Test7_RetryPreservesExecutionBindingAndSnapshots);

        await RunCheckAsync("Test 8: Interleaved worker read before recovery commit blocked by CAS and fencing token",
            Test8_InterleavedWorker_ReadBeforeRecovery_CommitBlockedByCasAndFencingToken);

        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"Kết quả kiểm thử Task T16: {passed} PASS, {failed} FAIL.");
        Console.WriteLine("=================================================================");

        if (failed > 0)
        {
            throw new InvalidOperationException($"Có {failed} ca kiểm thử Task T16 thất bại.");
        }

        return 0;
    }
}
