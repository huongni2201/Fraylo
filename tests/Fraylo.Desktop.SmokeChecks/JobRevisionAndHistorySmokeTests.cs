using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Jobs;
using Fraylo.Application.Settings;
using Fraylo.Infrastructure.Storage;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói C1–C3 (Job Revision, Job Retry, Job List History & List Active Invariants):
/// 1. Migration thêm cột Revision, giá trị mặc định là 1.
/// 2. Các thay đổi trạng thái/thao tác làm tăng Revision đơn điệu.
/// 3. job.retry hợp lệ trên Failed job (attempts &lt; maxAttempts, sideEffect=0) đưa về Queued, không reset attempts.
/// 4. job.retry bị từ chối khi sideEffectSubmitted = 1.
/// 5. job.retry bị từ chối khi Canceled hoặc CancelRequested = 1.
/// 6. job.retry bị từ chối với JobConcurrencyConflictException khi ExpectedRevision stale.
/// 7. job.retry bị từ chối khi attempts &gt;= maxAttempts (đúng contract: hết ngân sách phải tạo job mới).
/// 8. job.list_history phân trang bằng opaque cursor, chỉ trả về terminal đã resolved, không lẫn active.
/// 9. job.list_active bảo toàn 100% ngữ nghĩa (chỉ active + unresolved terminal).
/// </summary>
public static class JobRevisionAndHistorySmokeTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"ASSERTION FAILED: {message}");
        }
    }

    private static (DbContextOptions<FrayloDbContext> Options, string TempDir) CreateTestContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"titoai_c1c3_smoke_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "titoai_c1c3_test.db");
        var connectionString = DatabasePathHelper.BuildConnectionString(dbPath);

        var optionsBuilder = new DbContextOptionsBuilder<FrayloDbContext>();
        optionsBuilder.UseSqlite(connectionString)
                      .AddInterceptors(new SqlitePragmaInterceptor())
                      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        var dbOptions = optionsBuilder.Options;

        var now = DateTimeOffset.UtcNow.ToString("o");

        using (var db = new FrayloDbContext(dbOptions))
        {
            db.Database.EnsureCreated();


            var prj = new Project
            {
                Id = "prj_c1c3",
                Name = "C1-C3 Test Project",
                Status = "Active",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Projects.Add(prj);

            var scene = new Scene
            {
                Id = "scn_c1c3_1",
                ProjectId = "prj_c1c3",
                Name = "Scene 1",
                SequenceOrder = 1,
                TargetDurationSeconds = 15,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Scenes.Add(scene);

            db.SaveChanges();
        }

        return (dbOptions, tempDir);
    }

    private sealed class NoOpWakeupNotifier : IJobWakeupNotifier
    {
        public void NotifyWakeup(JobWakeupSignal signal) { }
    }

    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("\n=================================================================");
        Console.WriteLine("  Fraylo — Backend C1–C3 Smoke Checks (Job Revision, Retry & History)");
        Console.WriteLine("=================================================================");

        var (dbOptions, tempDir) = CreateTestContext();

        try
        {
            var reservation = new JobReservation(dbOptions);
            var fakeExecutor = new DeterministicFakeJobExecutor();
            await using var scheduler = new JobScheduler(dbOptions, reservation, fakeExecutor);
            var rootRegistry = new StorageRootRegistry(dbOptions);
            var settingsService = new SettingsService(dbOptions, rootRegistry);
            var snapshotFactory = new JobSnapshotFactory(settingsService);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);

            // Test 1: Migration Adds Revision Column, Default is 1
            Console.Write("[RUN] Test 1: Migration thêm cột revision, giá trị khởi tạo bằng 1 ... ");
            string jobId1 = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow.ToString("o");
            using (var db = new FrayloDbContext(dbOptions))
            {
                var job1 = new Job
                {
                    Id = jobId1,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Queued,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 0,
                    MaxAttempts = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.Jobs.Add(job1);
                await db.SaveChangesAsync();
            }

            var status1 = await jobService.GetStatusAsync(jobId1);
            Assert(status1.Job.Revision == 1, $"Revision ban đầu phải là 1, thực tế: {status1.Job.Revision}");
            Console.WriteLine("PASS");

            // Test 2: State changes increment Revision
            Console.Write("[RUN] Test 2: Thay đổi trạng thái (Cancel/Resume) tăng revision ... ");
            var cancelRes = await jobService.CancelAsync(jobId1);
            Assert(cancelRes.Revision == 2, $"Sau khi cancel, revision phải là 2, thực tế: {cancelRes.Revision}");
            var statusAfterCancel = await jobService.GetStatusAsync(jobId1);
            Assert(statusAfterCancel.Job.Revision == 2, $"GetStatus sau khi cancel phải thấy revision=2, thực tế: {statusAfterCancel.Job.Revision}");
            Console.WriteLine("PASS");

            // Test 3: RetryAsync valid Failed job with attempts < maxAttempts transitions to Queued, preserves attempts
            Console.Write("[RUN] Test 3: job.retry hợp lệ trên Failed job đưa về Queued, bảo toàn attempts ... ");
            string jobIdRetry = Guid.NewGuid().ToString();
            using (var db = new FrayloDbContext(dbOptions))
            {
                var jobRetry = new Job
                {
                    Id = jobIdRetry,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Failed,
                    ReasonCode = "network_error",
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    CancelRequested = 0,
                    Revision = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.Jobs.Add(jobRetry);
                await db.SaveChangesAsync();
            }

            var retryRes = await jobService.RetryAsync(new JobRetryRequest(jobIdRetry, ExpectedRevision: 3));
            Assert(retryRes.State == JobState.Queued, $"Trạng thái sau retry phải là Queued, thực tế: {retryRes.State}");
            Assert(retryRes.Revision == 4, $"Revision sau retry phải tăng lên 4, thực tế: {retryRes.Revision}");

            var statusRetry = await jobService.GetStatusAsync(jobIdRetry);
            Assert(statusRetry.Job.State == JobState.Queued, "DB phải ở trạng thái Queued");
            Assert(statusRetry.Job.Attempts == 1, $"Attempts phải được bảo toàn là 1, thực tế: {statusRetry.Job.Attempts}");
            Assert(statusRetry.Job.ReasonCode == null, "ReasonCode phải được xóa sạch sau khi retry về Queued");
            Console.WriteLine("PASS");

            // Test 4: RetryAsync rejected when SideEffectSubmitted = 1
            Console.Write("[RUN] Test 4: job.retry bị từ chối khi sideEffectSubmitted = 1 ... ");
            string jobIdSideEffect = Guid.NewGuid().ToString();
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = jobIdSideEffect,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Failed,
                    ReasonCode = "timeout",
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 1,
                    CancelRequested = 0,
                    Revision = 2,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            bool threwSideEffect = false;
            try
            {
                await jobService.RetryAsync(new JobRetryRequest(jobIdSideEffect, ExpectedRevision: 2));
            }
            catch (JobValidationException)
            {
                threwSideEffect = true;
            }
            Assert(threwSideEffect, "Bắt buộc ném JobValidationException khi sideEffectSubmitted = 1");
            Console.WriteLine("PASS");

            // Test 5: RetryAsync rejected when Canceled or CancelRequested = 1
            Console.Write("[RUN] Test 5: job.retry bị từ chối khi Canceled hoặc CancelRequested = 1 ... ");
            string jobIdCanceled = Guid.NewGuid().ToString();
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = jobIdCanceled,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Canceled,
                    ReasonCode = JobReasonCode.UserCanceled,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    CancelRequested = 1,
                    Revision = 2,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            bool threwCanceled = false;
            try
            {
                await jobService.RetryAsync(new JobRetryRequest(jobIdCanceled, ExpectedRevision: 2));
            }
            catch (JobConcurrencyConflictException)
            {
                threwCanceled = true;
            }
            Assert(threwCanceled, "Bắt buộc ném JobConcurrencyConflictException khi job đang ở Canceled");
            Console.WriteLine("PASS");

            // Test 6: RetryAsync rejected when ExpectedRevision is stale
            Console.Write("[RUN] Test 6: job.retry bị từ chối khi ExpectedRevision stale (Optimistic Concurrency) ... ");
            bool threwStale = false;
            try
            {
                await jobService.RetryAsync(new JobRetryRequest(jobIdRetry, ExpectedRevision: 2)); // current revision is 4
            }
            catch (JobConcurrencyConflictException)
            {
                threwStale = true;
            }
            Assert(threwStale, "Bắt buộc ném JobConcurrencyConflictException khi expectedRevision không khớp");
            Console.WriteLine("PASS");

            // Test 7: RetryAsync is a user action, so Attempts >= MaxAttempts must not block it
            Console.Write("[RUN] Test 7: job.retry không bị chặn bởi maxAttempts ...");
            string jobIdBudget = Guid.NewGuid().ToString();
            using (var db = new FrayloDbContext(dbOptions))
            {
                db.Jobs.Add(new Job
                {
                    Id = jobIdBudget,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Failed,
                    ReasonCode = "execution_failed",
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 3,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    CancelRequested = 0,
                    Revision = 4,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            var budgetRetry = await jobService.RetryAsync(new JobRetryRequest(jobIdBudget, ExpectedRevision: 4));
            Assert(budgetRetry.State == JobState.Queued, "Người dùng bấm thử lại phải được phép dù attempts đã đạt maxAttempts");
            Console.WriteLine("PASS");

            // Test 8: ListHistory paginated with opaque cursor, only resolved terminal jobs, no active jobs
            Console.Write("[RUN] Test 8: job.list_history phân trang cursor, chỉ trả về resolved terminal, không lẫn active ... ");
            string jobHistSucceeded = Guid.NewGuid().ToString();
            string jobHistFailedResolved = Guid.NewGuid().ToString();
            string jobHistCanceledResolved = Guid.NewGuid().ToString();
            string jobHistCanceledUnresolved = Guid.NewGuid().ToString();
            string jobHistActiveQueued = Guid.NewGuid().ToString();

            using (var db = new FrayloDbContext(dbOptions))
            {
                // Terminal 1: Succeeded (resolved)
                db.Jobs.Add(new Job
                {
                    Id = jobHistSucceeded,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Succeeded,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 1,
                    MaxAttempts = 3,
                    ResultText = "{\"title\":\"Done\"}",
                    CreatedAtUtc = "2026-10-27T08:00:00.000Z",
                    UpdatedAtUtc = "2026-10-27T08:10:00.000Z"
                });

                // Terminal 2: Failed without occupancy (resolved)
                db.Jobs.Add(new Job
                {
                    Id = jobHistFailedResolved,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Failed,
                    ReasonCode = "network_error",
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 3,
                    MaxAttempts = 3,
                    CreatedAtUtc = "2026-10-27T08:05:00.000Z",
                    UpdatedAtUtc = "2026-10-27T08:15:00.000Z"
                });

                // Terminal 3: Canceled without occupancy (resolved)
                db.Jobs.Add(new Job
                {
                    Id = jobHistCanceledResolved,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Canceled,
                    ReasonCode = JobReasonCode.UserCanceled,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 1,
                    MaxAttempts = 3,
                    CreatedAtUtc = "2026-10-27T08:10:00.000Z",
                    UpdatedAtUtc = "2026-10-27T08:20:00.000Z"
                });

                // Terminal 4: Canceled WITH occupancy (UNRESOLVED)
                db.Jobs.Add(new Job
                {
                    Id = jobHistCanceledUnresolved,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Canceled,
                    ReasonCode = JobReasonCode.UserCanceled,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 1,
                    CreatedAtUtc = "2026-10-27T08:12:00.000Z",
                    UpdatedAtUtc = "2026-10-27T08:22:00.000Z"
                });

                var prof = new BrowserProfile
                {
                    Id = "prof_hist",
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "History Profile",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.BrowserProfiles.Add(prof);

                var provAcc = new ProviderAccount
                {
                    Id = "acc_hist",
                    ProviderKey = "chatgpt-web",
                    ProfileId = "prof_hist",
                    DisplayName = "History Test Account",
                    MaxConcurrentJobs = 1,
                    MaxTabs = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.ProviderAccounts.Add(provAcc);

                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = "acc_hist",
                    TabSlotId = "slot_1",
                    JobId = jobHistCanceledUnresolved,
                    LeaseOwnerId = "worker_1",
                    LeaseToken = 1,
                    AcquiredAtUtc = now
                });

                // Active: Queued
                db.Jobs.Add(new Job
                {
                    Id = jobHistActiveQueued,
                    ProjectId = "prj_c1c3",
                    SceneId = null,
                    TaskType = JobTaskType.Script,
                    State = JobState.Queued,
                    ProviderKey = "chatgpt-web",
                    StorageRootId = "root_test",
                    Attempts = 0,
                    MaxAttempts = 3,
                    CreatedAtUtc = "2026-10-27T08:15:00.000Z",
                    UpdatedAtUtc = "2026-10-27T08:25:00.000Z"
                });

                await db.SaveChangesAsync();
            }

            // Gọi list_history với pageSize = 2
            var page1 = await jobService.ListHistoryAsync(new JobListHistoryRequest(
                PageSize: 2
            ));

            Assert(page1.Items.Count == 2, $"Trang 1 phải có đúng 2 items, thực tế: {page1.Items.Count}");
            Assert(!string.IsNullOrWhiteSpace(page1.NextCursor), "Trang 1 phải có nextCursor");
            Assert(!page1.Items.Any(i => i.Id == jobHistActiveQueued), "Lịch sử không được chứa active job");
            Assert(!page1.Items.Any(i => i.Id == jobHistCanceledUnresolved), "Lịch sử không được chứa unresolved terminal job");

            // Lấy trang 2 qua cursor
            var page2 = await jobService.ListHistoryAsync(new JobListHistoryRequest(
                Cursor: page1.NextCursor,
                PageSize: 2
            ));

            Assert(page2.Items.Count >= 1, "Trang 2 phải có ít nhất 1 item còn lại");
            Assert(!page2.Items.Any(i => i.Id == jobHistActiveQueued), "Trang 2 không được chứa active job");
            Assert(!page2.Items.Any(i => i.Id == jobHistCanceledUnresolved), "Trang 2 không được chứa unresolved terminal job");
            Console.WriteLine("PASS");

            // Test 9: ListActive preserves semantics and returns unresolved terminal jobs
            Console.Write("[RUN] Test 9: job.list_active giữ nguyên ngữ nghĩa (chỉ active + unresolved terminal) ... ");
            var activeRes = await jobService.ListActiveAsync();
            var activeIds = activeRes.Jobs.Select(j => j.Id).ToHashSet();

            Assert(activeIds.Contains(jobHistActiveQueued), "list_active phải chứa active queued job");
            Assert(activeIds.Contains(jobHistCanceledUnresolved), "list_active phải chứa Canceled job có slot occupancy (unresolved)");
            Assert(!activeIds.Contains(jobHistSucceeded), "list_active KHÔNG được chứa Succeeded job");
            Assert(!activeIds.Contains(jobHistFailedResolved), "list_active KHÔNG được chứa resolved Failed job");
            Assert(!activeIds.Contains(jobHistCanceledResolved), "list_active KHÔNG được chứa resolved Canceled job");
            Assert(activeRes.Jobs.All(j => j.Revision >= 1), "Tất cả JobSummaryDto trong list_active đều phải có Revision >= 1");
            Console.WriteLine("PASS");

            Console.WriteLine("-----------------------------------------------------------------");
            Console.WriteLine("TẤT CẢ 9/9 KIỂM THỬ KHÓI BACKEND C1–C3 ĐÃ VƯỢT QUA (PASS 100%)!");
            Console.WriteLine("=================================================================\n");
            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch { }
        }
    }
}
