using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using TiToAI.Application.Accounts;
using TiToAI.Application.Jobs;
using TiToAI.Application.Providers;
using TiToAI.Application.Settings;
using TiToAI.Browser;
using TiToAI.Browser.Abstractions;
using TiToAI.Browser.Models;
using TiToAI.Contracts.Accounts;
using TiToAI.Contracts.Bridge;
using TiToAI.Contracts.Jobs;
using TiToAI.Contracts.Providers;
using TiToAI.Desktop.Bridge.Handlers;
using TiToAI.Desktop.Hosting;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;
using TiToAI.Infrastructure.Persistence.Interceptors;
using TiToAI.Infrastructure.Storage;

namespace TiToAI.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói định lượng xác minh:
/// 1. Giới hạn đồng thời N của tài khoản AI (MaxTabs làm nguồn cấu hình duy nhất, bỏ MaxJobs).
/// 2. Tạo/sửa tài khoản, lưu và reload giữ đúng N.
/// 3. N=2 với 5 job đủ điều kiện: tối đa 2 job chạy trên 2 tab; còn lại chờ; một job xong thì job tiếp theo được chạy.
/// 4. Tài khoản cũ có Max Jobs khác Max Tabs dùng Max Tabs làm giới hạn mới, không bị Max Jobs cũ chặn.
/// 5. Giảm N khi đang có job chạy: không hủy job đang chạy, dừng nhận thêm đến khi còn capacity.
/// 6. Vòng đời browser:
///    - Không tự mở browser khi mở app, load tài khoản hoặc chỉ vì tài khoản bật nhận việc.
///    - Chỉ mở browser automation khi đã reserve job đủ điều kiện, chạy ẩn.
///    - Mỗi job 1 tab riêng, cùng account dùng chung browser/profile.
///    - Hết job thì browser đóng và giải phóng tài nguyên.
///    - Lưu profile/cookie để lần chạy sau sử dụng lại phiên đăng nhập.
///    - Không đóng browser khi còn job chạy hoặc job chưa rõ kết quả cần đối soát.
///    - An toàn khi job mới đến trong lúc browser đang đóng, không tạo 2 browser cùng sở hữu 1 profile.
/// </summary>
public static class AccountConcurrencyAndBrowserLifecycleSmokeTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] {message}");
        }
    }

    private static (DbContextOptions<TiToAIDbContext> Options, string TempDir, StorageRootRegistry Registry, SettingsService Settings) CreateTestContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"titoai_concurrency_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "concurrency.db");
        var connectionString = DatabasePathHelper.BuildConnectionString(dbPath);

        var optionsBuilder = new DbContextOptionsBuilder<TiToAIDbContext>();
        optionsBuilder.UseSqlite(connectionString)
                      .AddInterceptors(new SqlitePragmaInterceptor())
                      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        var dbOptions = optionsBuilder.Options;

        using (var db = new TiToAIDbContext(dbOptions))
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
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch { }
    }

    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("\n=================================================================");
        Console.WriteLine("    TiToAI — Account Concurrency & Browser Lifecycle Smoke Tests ");
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
            "1. Tạo/sửa tài khoản với MaxTabs làm N duy nhất, lưu và reload giữ đúng N",
            Test_AccountConcurrency_CreateAndUpdate_PersistsNAsync);

        await RunCaseAsync(
            "2. N=2 với 5 job đủ điều kiện: tối đa 2 job chạy trên 2 tab; job xong thì job tiếp được chạy",
            Test_AccountConcurrency_N2_With5Jobs_StreamingNextAsync);

        await RunCaseAsync(
            "3. Tài khoản cũ có MaxJobs=1 khác MaxTabs=3: dùng MaxTabs=3 làm giới hạn mới, không bị chặn ngầm",
            Test_AccountConcurrency_LegacyAccount_UsesMaxTabsAsCapAsync);

        await RunCaseAsync(
            "4. Giảm N khi đang có job chạy: không hủy job đang chạy, dừng nhận thêm đến khi còn capacity",
            Test_AccountConcurrency_ReduceNDuringRunningJobsAsync);

        await RunCaseAsync(
            "5. Vòng đời browser: Không mở khi mở app; có job thì mở ẩn; hết job thì đóng; chạy tiếp giữ phiên",
            Test_BrowserLifecycle_FullLifecycleWithSavedSessionAsync);

        await RunCaseAsync(
            "6. Vòng đời browser: An toàn khi job mới đến trong lúc browser đang đóng, không tạo 2 browser",
            Test_BrowserLifecycle_JobArrivalDuringClosing_SafeAsync);

        await RunCaseAsync(
            "7. Vòng đời browser: Không đóng browser khi còn job chạy hoặc job chưa rõ kết quả đối soát",
            Test_BrowserLifecycle_DoesNotCloseWhenJobActiveOrUncertainAsync);

        await RunCaseAsync(
            "8. Vòng đời browser: Nhiều job cùng profile xin lease đồng thời chỉ mở một browser",
            Test_BrowserLaunch_ConcurrentLeasesOnSameProfile_LaunchOnceAsync);

        await RunCaseAsync(
            "9. Đối soát: Job chờ bị slot của job hủy chưa đối soát giữ phải báo waiting_for_reconcile",
            Test_Reservation_HeldByUnreconciledCancel_ReportsWaitingForReconcileAsync);

        await RunCaseAsync(
            "10. Render MuseAI hết giờ: Failed và giải phóng; retry chỉ tiếp tục chờ side chat đã lưu, không gửi lại",
            Test_RenderTimeout_FailsAndRetryResumesWithoutResendAsync);

        await RunCaseAsync(
            "11. Một tài khoản MuseAI, một tab: render đầu kết thúc theo mọi nhánh thì render kế tiếp vẫn được chạy",
            Test_SingleMuseTab_NextRenderRunsAfterFirstEndsAsync);

        await RunCaseAsync(
            "12. Trình duyệt thật: đóng context khi hết job phải đóng cả tab blank mặc định",
            Test_RealBrowser_CloseContextClosesDefaultBlankTabAsync);

        await RunCaseAsync(
            "13. Trình duyệt thật + scheduler: job xong, không còn job nào thì browser phải đóng",
            Test_RealBrowser_JobFinishedAndIdle_ClosesBrowserAsync);

        await RunCaseAsync(
            "14. Job MuseAI chờ đối soát không giữ browser; job ChatGPT chờ đối soát vẫn giữ",
            Test_BrowserIdle_AwaitingMuseAiJobDoesNotKeepBrowserOpenAsync);

        Console.WriteLine("\n=================================================================");
        if (failed == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"   TẤT CẢ {passed}/{passed} BÀI KIỂM THỬ GIỚI HẠN ĐỒNG THỜI & BROWSER ĐÃ PASS!");
            Console.ResetColor();
            Console.WriteLine("=================================================================");
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   THẤT BẠI: {failed} FAIL, {passed} PASS");
            Console.ResetColor();
            Console.WriteLine("=================================================================");
            return 1;
        }
    }

    /// <summary>
    /// Kịch bản 1: Tạo/sửa tài khoản, lưu và reload vẫn giữ đúng N.
    /// Kiểm tra nguồn cấu hình duy nhất là MaxTabs, MaxConcurrentJobs được đồng bộ với N.
    /// </summary>
    public static async Task Test_AccountConcurrency_CreateAndUpdate_PersistsNAsync()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var identityResolver = new FakeProfileIdentityResolver("Thomas (123456)");
            var accountService = new ProviderAccountService(dbOptions, identityResolver);

            // 1. Tạo tài khoản chỉ truyền MaxTabs = 2 (ô MaxJobs bị loại khỏi form)
            var createReq = new CreateProviderAccountRequest(
                ProviderKey: "chatgpt-web",
                DisplayName: "ChatGPT Team N2",
                MaxTabs: 2
            );
            var created = await accountService.CreateAccountAsync(createReq);

            Assert(created.MaxTabs == 2, $"Kỳ vọng MaxTabs = 2, thực tế {created.MaxTabs}");
            Assert(created.MaxConcurrentJobs == 2, $"Kỳ vọng MaxConcurrentJobs tự động đồng bộ = 2, thực tế {created.MaxConcurrentJobs}");

            // 2. Xác minh trong DB thật
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var dbAcc = await db.ProviderAccounts.FirstAsync(a => a.Id == created.Id);
                Assert(dbAcc.MaxTabs == 2, "DB phải lưu MaxTabs = 2");
                Assert(dbAcc.MaxConcurrentJobs == 2, "DB phải lưu MaxConcurrentJobs = 2");
            }

            // 3. Giả lập xác minh danh tính để có thể kích hoạt
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var acc = await db.ProviderAccounts.FirstAsync(a => a.Id == created.Id);
                acc.VerifiedIdentity = "team@chatgpt.internal";
                acc.IdentityVerifiedAtUtc = DateTime.UtcNow.ToString("o");
                await db.SaveChangesAsync();
            }

            // 4. Sửa tài khoản đổi N lên 4
            var updateReq = new UpdateProviderAccountRequest(
                Id: created.Id,
                DisplayName: "ChatGPT Team N4",
                Enabled: true,
                MaxTabs: 4,
                MaxConcurrentJobs: 4,
                PerTaskLimitsJson: null,
                ExpectedRevision: created.Revision
            );
            var updated = await accountService.UpdateAccountAsync(updateReq);

            Assert(updated.MaxTabs == 4, $"Kỳ vọng MaxTabs sau sửa = 4, thực tế {updated.MaxTabs}");
            Assert(updated.MaxConcurrentJobs == 4, $"Kỳ vọng MaxConcurrentJobs sau sửa = 4, thực tế {updated.MaxConcurrentJobs}");

            // 5. Reload lại danh sách tài khoản
            var reloadedList = await accountService.ListAccountsAsync();
            var reloadedAcc = reloadedList.First(a => a.Id == created.Id);
            Assert(reloadedAcc.MaxTabs == 4, $"Sau reload, MaxTabs phải duy trì = 4, thực tế {reloadedAcc.MaxTabs}");
            Assert(reloadedAcc.MaxConcurrentJobs == 4, "Sau reload, MaxConcurrentJobs phải = 4");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 2: N=2 với 5 job đủ điều kiện và budget đủ:
    /// Tối đa 2 job chạy trên 2 tab; còn lại chờ; một job xong thì job tiếp theo được chạy.
    /// </summary>
    public static async Task Test_AccountConcurrency_N2_With5Jobs_StreamingNextAsync()
    {
        var (dbOptions, tempDir, _, settingsService) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");

            // Tạo tài khoản với N = 2 (MaxTabs = 2, MaxConcurrentJobs = 2)
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "profile_chatgpt_n2",
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "ChatGPT N2 Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc-chatgpt-n2",
                    ProviderKey = "chatgpt-web",
                    ProfileId = "profile_chatgpt_n2",
                    DisplayName = "ChatGPT N2 Account",
                    VerifiedIdentity = "team@chatgpt.internal",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 2,
                    MaxTabs = 2,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.AppSettings.Add(new AppSetting
                {
                    Key = SettingsService.KeyScriptProvider,
                    Value = "chatgpt-web",
                    UpdatedAtUtc = now
                });

                // Tạo 5 Project độc lập.
                for (int i = 1; i <= 5; i++)
                {
                    db.Projects.Add(new Project
                    {
                        Id = $"prj_n2_{i}",
                        Name = $"Dự án {i}",
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

            // Enqueue 5 jobs đủ điều kiện
            var jobIds = new List<string>();
            for (int i = 1; i <= 5; i++)
            {
                var res = await jobService.EnqueueAsync(new JobEnqueueRequest(
                    ProjectId: $"prj_n2_{i}",
                    SceneId: null,
                    TaskType: JobTaskType.Script,
                    CustomPromptText: $"Kịch bản {i}"
                ));
                jobIds.Add(res.JobId);
            }

            // Dispatch đợt 1: Do N = 2, tối đa chỉ đúng 2 jobs được dispatch
            int dispatched1 = await scheduler.DispatchPendingJobsAsync();
            Assert(dispatched1 == 2, $"Kỳ vọng đúng 2 jobs được dispatch theo N=2, thực tế: {dispatched1}");

            // Chờ các worker chuyển trạng thái sang Running
            for (int wait = 0; wait < 100; wait++)
            {
                await using var db = new TiToAIDbContext(dbOptions);
                var runningCount = await db.Jobs.Where(j => j.State == JobState.Running).CountAsync();
                if (runningCount == 2) break;
                await Task.Delay(20);
            }

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var running = await db.Jobs.Where(j => j.State == JobState.Running).ToListAsync();
                var queued = await db.Jobs.Where(j => j.State == JobState.Queued).ToListAsync();
                Assert(running.Count == 2, $"Phải có đúng 2 jobs đang Running, thực tế {running.Count}");
                Assert(queued.Count == 3, $"3 jobs còn lại phải ở trạng thái Queued, thực tế {queued.Count}");
                Assert(queued.All(j => j.Attempts == 0), "Job chờ không được tăng attempts");

                var occupancies = await db.ActiveSlotOccupancies.ToListAsync();
                Assert(occupancies.Count == 2, $"Phải có đúng 2 slot occupancy trong DB, thực tế {occupancies.Count}");
                var slots = occupancies.Select(o => o.TabSlotId).OrderBy(s => s).ToList();
                Assert(slots[0] == "slot-1" && slots[1] == "slot-2", "Hai slot phải là slot-1 và slot-2 riêng biệt");
            }

            // Hoàn tất 1 job đang chạy
            string firstRunningId;
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                firstRunningId = (await db.Jobs.FirstAsync(j => j.State == JobState.Running)).Id;
            }
            controlledExecutor.CompleteJob(firstRunningId);

            // Chờ worker nhả slot
            for (int wait = 0; wait < 50; wait++)
            {
                await Task.Delay(20);
                await using var db = new TiToAIDbContext(dbOptions);
                var j = await db.Jobs.FirstAsync(x => x.Id == firstRunningId);
                if (j.State == JobState.Succeeded) break;
            }

            // Dispatch đợt 2: Slot vừa rảnh được cấp ngay cho job thứ 3 đang chờ
            int dispatched2 = await scheduler.DispatchPendingJobsAsync();
            Assert(dispatched2 == 1, $"Kỳ vọng đúng 1 job tiếp theo được chạy, thực tế: {dispatched2}");

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var runningCount = await db.Jobs.CountAsync(j => j.State == JobState.Running);
                var succeededCount = await db.Jobs.CountAsync(j => j.State == JobState.Succeeded);
                var queuedCount = await db.Jobs.CountAsync(j => j.State == JobState.Queued);

                Assert(runningCount == 2, $"Tiếp tục duy trì tối đa 2 jobs chạy, thực tế {runningCount}");
                Assert(succeededCount == 1, "Đã có 1 job hoàn tất");
                Assert(queuedCount == 2, $"Còn 2 jobs đang chờ, thực tế {queuedCount}");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Job đã hủy nhưng side effect đã gửi và chưa đối soát vẫn giữ slot: job chờ phải báo waiting_for_reconcile, không báo waiting_for_capacity.
    /// </summary>
    public static async Task Test_Reservation_HeldByUnreconciledCancel_ReportsWaitingForReconcileAsync()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            string holderId = Guid.NewGuid().ToString("D");
            string waitingId = Guid.NewGuid().ToString("D");

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "profile_muse_reconcile",
                    Kind = "SharedAI",
                    ProviderKey = "museai",
                    DisplayName = "Muse Reconcile Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc-muse-reconcile",
                    ProviderKey = "museai",
                    ProfileId = "profile_muse_reconcile",
                    DisplayName = "Muse Reconcile Account",
                    VerifiedIdentity = "muse-reconcile@internal",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 1,
                    MaxTabs = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.Projects.Add(new Project
                {
                    Id = "prj_reconcile",
                    Name = "Dự án đối soát",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.Jobs.Add(new Job
                {
                    Id = holderId,
                    ProjectId = "prj_reconcile",
                    TaskType = JobTaskType.VideoRender,
                    State = JobState.Canceled,
                    Attempts = 1,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 1,
                    SideEffectAtUtc = now,
                    CancelRequested = 1,
                    StorageRootId = "root_test",
                    ProviderKey = "museai",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.Jobs.Add(new Job
                {
                    Id = waitingId,
                    ProjectId = "prj_reconcile",
                    TaskType = JobTaskType.VideoRender,
                    State = JobState.Queued,
                    Attempts = 0,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    StorageRootId = "root_test",
                    ProviderKey = "museai",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.ActiveSlotOccupancies.Add(new ActiveSlotOccupancy
                {
                    ProviderAccountId = "acc-muse-reconcile",
                    TabSlotId = "slot-1",
                    JobId = holderId,
                    LeaseOwnerId = "scheduler-test",
                    LeaseToken = 1,
                    AcquiredAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            var reservation = new JobReservation(dbOptions);
            var result = await reservation.TryReserveJobAsync(waitingId, "worker-test", TimeSpan.FromMinutes(5));
            Assert(!result.Acquired, "Job chờ không được chiếm slot đang bị job cần đối soát giữ.");

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var waiting = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == waitingId);
                Assert(waiting.ReasonCode == JobReasonCode.WaitingForReconcile,
                    $"ReasonCode phải là waiting_for_reconcile khi slot do job chưa đối soát giữ, thực tế: {waiting.ReasonCode}");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Render MuseAI hết thời gian chờ: job chuyển Failed (không AwaitingUser). Retry chỉ được phép khi có side chat đã lưu,
    /// khi đó chỉ tiếp tục chờ video (không gửi lại prompt). Không có side chat đã lưu thì từ chối retry.
    /// </summary>
    public static async Task Test_RenderTimeout_FailsAndRetryResumesWithoutResendAsync()
    {
        var (dbOptions, tempDir, _, settingsService) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            string resumableId = Guid.NewGuid().ToString("D");
            string unsavedId = Guid.NewGuid().ToString("D");
            string threadUrl = $"https://muse.ai/thread/{Guid.NewGuid():D}";

            // Chính sách: timeout đã gửi hoặc chưa gửi đều thành Failed, không tự retry, không đưa về AwaitingUser.
            var policyJob = new Job { Id = resumableId, TaskType = JobTaskType.VideoRender, ProviderKey = ProviderKeys.MuseAI, State = JobState.Running, Attempts = 1, MaxAttempts = 3, SideEffectSubmitted = 1 };
            var submittedDecision = JobRetryAndCancelPolicy.EvaluateFailure(policyJob, JobReasonCode.RenderTimeout, true);
            Assert(submittedDecision.TargetState == JobState.Failed && !submittedDecision.CanRetry,
                "Render hết giờ sau khi gửi phải chuyển Failed, không được AwaitingUser hay tự retry.");
            var preSubmitDecision = JobRetryAndCancelPolicy.EvaluateFailure(
                new Job { Id = unsavedId, TaskType = JobTaskType.VideoRender, ProviderKey = ProviderKeys.MuseAI, State = JobState.Running, Attempts = 1, MaxAttempts = 3, SideEffectSubmitted = 0 },
                JobReasonCode.RenderTimeout, false);
            Assert(preSubmitDecision.TargetState == JobState.Failed && !preSubmitDecision.CanRetry,
                "Render hết giờ trước khi gửi cũng phải chuyển Failed.");

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.Projects.Add(new Project { Id = "prj_timeout", Name = "Dự án timeout", Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now });
                // Cả hai job đã gửi prompt (SideEffectSubmitted = 1); chỉ job đầu có side chat đã lưu.
                foreach (var id in new[] { resumableId, unsavedId })
                {
                    db.Jobs.Add(new Job
                    {
                        Id = id,
                        ProjectId = "prj_timeout",
                        TaskType = JobTaskType.VideoRender,
                        ProviderKey = ProviderKeys.MuseAI,
                        State = JobState.Failed,
                        ReasonCode = JobReasonCode.RenderTimeout,
                        Attempts = 1,
                        MaxAttempts = 3,
                        Revision = 1,
                        SideEffectSubmitted = 1,
                        StorageRootId = "root_test",
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }
                db.AppSettings.Add(new AppSetting { Key = "job.museai.thread." + resumableId, Value = threadUrl, UpdatedAtUtc = now });
                await db.SaveChangesAsync();
            }

            var profileRegistry = new ProfileRegistry(() => new TiToAIDbContext(dbOptions), tempDir);
            var profileOwner = new ProfileOwner(pid => Path.Combine(tempDir, "profiles", pid));
            var processController = new PlaywrightBrowserController(profileRegistry, profileOwner,
                customPersistentLauncher: new TrackingPersistentLauncher().LaunchAsync);
            var gatekeeper = new HostBrowserGatekeeper(profileRegistry, new LoginCoordinator(profileRegistry, processController), new TabPool(), processController);
            var scheduler = new JobScheduler(dbOptions, new JobReservation(dbOptions), new ControlledJobExecutor(), gatekeeper);
            var jobService = new JobService(dbOptions, new JobSnapshotFactory(settingsService), scheduler);

            // Retry tác vụ đã có side chat đã lưu: đưa về Queued, giữ nguyên số lần thử; executor sẽ tiếp tục chờ, không gửi lại.
            var retried = await jobService.RetryAsync(new JobRetryRequest(resumableId, 1));
            Assert(retried.State == JobState.Queued, $"Retry render hết giờ có side chat phải về Queued, thực tế: {retried.State}");

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var resumable = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == resumableId);
                Assert(resumable.SideEffectSubmitted == 1, "Retry không được xóa cờ side effect đã gửi.");
                Assert(await MuseAiPlaywrightAutomation.CanResumeRenderAsync(db, resumable, CancellationToken.None),
                    "Job có side chat đã lưu phải được nhận diện là có thể tiếp tục chờ.");
            }

            // Không có side chat đã lưu: không được retry, tránh gửi lại prompt khi chưa rõ đã gửi hay chưa.
            bool refused = false;
            try
            {
                await jobService.RetryAsync(new JobRetryRequest(unsavedId, 1));
            }
            catch (JobValidationException)
            {
                refused = true;
            }
            Assert(refused, "Retry render đã gửi nhưng thiếu side chat đã lưu phải bị từ chối.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Một tài khoản MuseAI với MaxTabs = 1: mỗi nhánh kết thúc của render đầu tiên không được giữ tab/slot làm
    /// render kế tiếp bị chặn vĩnh viễn. Render đầu có thể kết thúc bằng timeout, lỗi sau khi gửi, thành công hoặc timeout trước khi gửi.
    /// </summary>
    public static async Task Test_SingleMuseTab_NextRenderRunsAfterFirstEndsAsync()
    {
        // ExpectNext = false chỉ dành cho lỗi chưa lưu side chat sau khi gửi: chưa rõ prompt đã vào chat, phải đối soát có chủ đích.
        var scenarios = new (string Name, Func<Func<Task>, Task<JobExecutionResult>> Script, bool ExpectNext)[]
        {
            ("render_timeout sau khi gửi", commit => Submitted(commit, new JobExecutionResult(false, ReasonCode: JobReasonCode.RenderTimeout, SideEffectSubmitted: true)), true),
            ("gián đoạn sau khi đã lưu side chat", commit => Submitted(commit, new JobExecutionResult(false, ReasonCode: JobReasonCode.RenderInterrupted, SideEffectSubmitted: true)), true),
            ("thành công", commit => Submitted(commit, new JobExecutionResult(true, ResultReference: "asset-first", SideEffectSubmitted: true)), true),
            ("render_timeout trước khi gửi", commit => Task.FromResult(new JobExecutionResult(false, ReasonCode: JobReasonCode.RenderTimeout, SideEffectSubmitted: false)), true),
            ("lỗi chưa lưu side chat sau khi gửi (reconcile_required)", commit => Submitted(commit, new JobExecutionResult(false, ReasonCode: JobReasonCode.ReconcileRequired, SideEffectSubmitted: true)), false),
        };

        var blocked = new List<string>();
        foreach (var (name, script, expectNext) in scenarios)
        {
            var (ran, firstState, reason) = await RunSingleMuseTabScenarioAsync(script);
            if (ran != expectNext)
            {
                blocked.Add($"[{name}] render đầu ở trạng thái '{firstState}', render kế tiếp chạy={ran}, mong đợi={expectNext} (reason='{reason}')");
            }
        }

        Assert(blocked.Count == 0,
            "Một tab MuseAI bị chiếm sau khi render kết thúc:\n  - " + string.Join("\n  - ", blocked));
    }

    private static Task<JobExecutionResult> Submitted(Func<Task> commit, JobExecutionResult result) =>
        commit().ContinueWith(_ => result, TaskScheduler.Default);

    private static async Task<(bool Ran, string FirstState, string? Reason)> RunSingleMuseTabScenarioAsync(
        Func<Func<Task>, Task<JobExecutionResult>> firstScript)
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            var earlier = DateTimeOffset.UtcNow.AddSeconds(-5).ToString("o");
            const string profileId = "profile_muse_single";
            string firstId = Guid.NewGuid().ToString("D");
            string secondId = Guid.NewGuid().ToString("D");

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = profileId,
                    Kind = "SharedAI",
                    ProviderKey = "museai",
                    DisplayName = "Muse một tab",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc-muse-single",
                    ProviderKey = "museai",
                    ProfileId = profileId,
                    DisplayName = "Muse một tab",
                    VerifiedIdentity = "muse-single@internal",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 1,
                    MaxTabs = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.Projects.Add(new Project
                {
                    Id = "prj_muse_single",
                    Name = "Dự án một tab",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.Jobs.Add(new Job
                {
                    Id = firstId,
                    ProjectId = "prj_muse_single",
                    TaskType = JobTaskType.VideoRender,
                    ProviderKey = ProviderKeys.MuseAI,
                    State = JobState.Queued,
                    Attempts = 0,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    StorageRootId = "root_test",
                    CreatedAtUtc = earlier,
                    UpdatedAtUtc = earlier
                });
                db.Jobs.Add(new Job
                {
                    Id = secondId,
                    ProjectId = "prj_muse_single",
                    TaskType = JobTaskType.VideoRender,
                    ProviderKey = ProviderKeys.MuseAI,
                    State = JobState.Queued,
                    Attempts = 0,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    StorageRootId = "root_test",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            var executor = new ScriptedRenderExecutor(firstId, firstScript);
            var scheduler = new JobScheduler(dbOptions, new JobReservation(dbOptions), executor);

            // Render đầu giữ tab cho tới khi test cho phép kết thúc; nếu không, worker nhả slot trước khi dispatch vòng sau.
            int firstDispatched = await scheduler.DispatchPendingJobsAsync();
            Assert(firstDispatched == 1, $"Chỉ render đầu được dispatch khi còn một tab, thực tế: {firstDispatched}");

            executor.Release.TrySetResult();
            string firstState = JobState.Running;
            for (int wait = 0; wait < 200; wait++)
            {
                await Task.Delay(20);
                await using var db = new TiToAIDbContext(dbOptions);
                firstState = (await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == firstId)).State;
                if (firstState is not (JobState.Reserved or JobState.Running)) break;
            }

            int secondDispatched = await scheduler.DispatchPendingJobsAsync();
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var second = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == secondId);
                return (secondDispatched == 1, firstState, second.ReasonCode);
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Trình duyệt Playwright thật: khi mở context, Chromium có sẵn một tab about:blank. Job đóng tab của nó thì tab blank vẫn còn;
    /// đóng context phải đóng luôn trình duyệt, không được để lại tab blank.
    /// </summary>
    public static async Task Test_RealBrowser_CloseContextClosesDefaultBlankTabAsync()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            const string profileId = "profile_real_close";

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = profileId,
                    Kind = "SharedAI",
                    ProviderKey = "museai",
                    DisplayName = "Real close",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            var profileRegistry = new ProfileRegistry(() => new TiToAIDbContext(dbOptions), tempDir);
            var profileOwner = new ProfileOwner(pid => Path.Combine(tempDir, "profiles", pid));
            var controller = new PlaywrightBrowserController(profileRegistry, profileOwner);

            var handle = await controller.LaunchPersistentContextAsync(profileId, isHeadless: true);
            var context = handle.Context;
            int blankAtLaunch = context.Pages.Count;

            var jobPage = await context.NewPageAsync();
            await jobPage.CloseAsync();
            int blankAfterJob = context.Pages.Count;

            await controller.CloseContextAsync(profileId);

            bool contextStillAlive;
            try
            {
                await context.NewPageAsync();
                contextStillAlive = true;
            }
            catch (PlaywrightException)
            {
                contextStillAlive = false;
            }

            Assert(blankAtLaunch >= 1, $"Khi mở context phải có tab blank mặc định, thực tế: {blankAtLaunch}");
            Assert(blankAfterJob >= 1, $"Tab blank mặc định vẫn còn sau khi job đóng tab của nó, thực tế: {blankAfterJob}");
            Assert(!controller.IsRunning(profileId), "Sau CloseContextAsync, trình duyệt không được còn được theo dõi là đang chạy.");
            Assert(!contextStillAlive, "CloseContextAsync phải đóng cả context và trình duyệt, không được còn tab blank mặc định.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Luồng thật: scheduler + gatekeeper + trình duyệt Playwright thật. Job mở một tab như MuseAI rồi đóng tab đó.
    /// Khi không còn job nào, browser phải đóng hẳn (kể cả tab blank mặc định), không chỉ đóng tab của job.
    /// </summary>
    public static async Task Test_RealBrowser_JobFinishedAndIdle_ClosesBrowserAsync()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            const string profileId = "profile_real_job";
            const string providerKey = "smoke-browser";
            string jobId = Guid.NewGuid().ToString("D");

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = profileId,
                    Kind = "SharedAI",
                    ProviderKey = providerKey,
                    DisplayName = "Real job",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc-real-job",
                    ProviderKey = providerKey,
                    ProfileId = profileId,
                    DisplayName = "Real job",
                    VerifiedIdentity = "real-job@internal",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 1,
                    MaxTabs = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.Projects.Add(new Project { Id = "prj_real_job", Name = "Real job", Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.Jobs.Add(new Job
                {
                    Id = jobId,
                    ProjectId = "prj_real_job",
                    TaskType = JobTaskType.VideoRender,
                    ProviderKey = providerKey,
                    State = JobState.Queued,
                    Attempts = 0,
                    MaxAttempts = 3,
                    SideEffectSubmitted = 0,
                    StorageRootId = "root_test",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            var profileRegistry = new ProfileRegistry(() => new TiToAIDbContext(dbOptions), tempDir);
            var profileOwner = new ProfileOwner(pid => Path.Combine(tempDir, "profiles", pid));
            var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
            var gatekeeper = new HostBrowserGatekeeper(profileRegistry, new LoginCoordinator(profileRegistry, processController), new TabPool(), processController);
            var scheduler = new JobScheduler(dbOptions, new JobReservation(dbOptions), new TabOpeningExecutor(processController), gatekeeper);

            int dispatched = await scheduler.DispatchPendingJobsAsync();
            Assert(dispatched == 1, $"Job phải được dispatch, thực tế: {dispatched}");

            string state = JobState.Queued;
            for (int wait = 0; wait < 300; wait++)
            {
                await Task.Delay(50);
                await using var db = new TiToAIDbContext(dbOptions);
                state = (await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId)).State;
                if (JobState.IsTerminal(state)) break;
            }
            Assert(state == JobState.Succeeded, $"Job phải Succeeded, thực tế: {state}");

            // Đóng browser diễn ra nền sau khi worker kết thúc; cho tối đa 10 giây.
            for (int wait = 0; wait < 200 && processController.IsRunning(profileId); wait++)
            {
                await Task.Delay(50);
            }

            Assert(!processController.IsRunning(profileId),
                "Không còn job nào trên profile thì browser phải đóng, nhưng browser vẫn đang chạy (còn tab blank mặc định).");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>Executor giả lập MuseAI: mở một tab trên context của profile rồi đóng tab đó trước khi kết thúc.</summary>
    private sealed class TabOpeningExecutor : IJobExecutor
    {
        private readonly IBrowserProcessController _controller;

        public TabOpeningExecutor(IBrowserProcessController controller) => _controller = controller;

        public async Task<JobExecutionResult> ExecuteAsync(
            JobExecutionContext context,
            Func<Task> onPreSideEffectCommitAsync,
            Func<int, string, Task>? onProgressAsync,
            CancellationToken cancellationToken)
        {
            var handle = _controller.TryGetContext(context.ProfileId)
                ?? throw new InvalidOperationException("Profile chưa có browser context cho job.");
            var page = await handle.Context.NewPageAsync();
            await page.CloseAsync();
            return new JobExecutionResult(true, ResultReference: "tab-used", SideEffectSubmitted: true);
        }
    }

    /// <summary>
    /// Job AwaitingUser đã gửi (chưa đối soát) trong DB: MuseAI không giữ browser vì side chat đã lưu và khôi phục mở lại được;
    /// ChatGPT vẫn giữ browser như trước.
    /// </summary>
    public static async Task Test_BrowserIdle_AwaitingMuseAiJobDoesNotKeepBrowserOpenAsync()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            const string museProfile = "profile_muse_awaiting";
            const string chatProfile = "profile_chat_awaiting";

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.Projects.Add(new Project { Id = "prj_awaiting", Name = "Chờ đối soát", Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now });
                foreach (var (profileId, accountId, providerKey, taskType, jobId) in new[]
                {
                    (museProfile, "acc-muse-awaiting", ProviderKeys.MuseAI, JobTaskType.VideoRender, "job-muse-awaiting"),
                    (chatProfile, "acc-chat-awaiting", ProviderKeys.ChatGPTWeb, JobTaskType.Script, "job-chat-awaiting"),
                })
                {
                    db.BrowserProfiles.Add(new BrowserProfile
                    {
                        Id = profileId,
                        Kind = "SharedAI",
                        ProviderKey = providerKey,
                        DisplayName = profileId,
                        SessionStatus = "Connected",
                        OperationalState = "Stopped",
                        VisibilityState = "Hidden",
                        Lifecycle = "Active",
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                    db.ProviderAccounts.Add(new ProviderAccount
                    {
                        Id = accountId,
                        ProviderKey = providerKey,
                        ProfileId = profileId,
                        DisplayName = accountId,
                        VerifiedIdentity = accountId + "@internal",
                        IdentityVerifiedAtUtc = now,
                        Enabled = true,
                        Lifecycle = "Active",
                        MaxConcurrentJobs = 1,
                        MaxTabs = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                    db.Jobs.Add(new Job
                    {
                        Id = jobId,
                        ProjectId = "prj_awaiting",
                        TaskType = taskType,
                        ProviderKey = providerKey,
                        State = JobState.AwaitingUser,
                        ReasonCode = JobReasonCode.ReconcileRequired,
                        Attempts = 1,
                        MaxAttempts = 3,
                        SideEffectSubmitted = 1,
                        StorageRootId = "root_test",
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                    db.ExecutionBindings.Add(new ExecutionBinding
                    {
                        JobId = jobId,
                        ProfileId = profileId,
                        ProviderAccountId = accountId,
                        TabSlotId = "slot-1",
                        AssignedAtUtc = now
                    });
                }
                await db.SaveChangesAsync();
            }

            var profileRegistry = new ProfileRegistry(() => new TiToAIDbContext(dbOptions), tempDir);
            var profileOwner = new ProfileOwner(pid => Path.Combine(tempDir, "profiles", pid));
            var fakeLauncher = new TrackingPersistentLauncher();
            var processController = new PlaywrightBrowserController(profileRegistry, profileOwner, customPersistentLauncher: fakeLauncher.LaunchAsync);
            var tabPool = new TabPool();
            var loginCoordinator = new LoginCoordinator(profileRegistry, processController);
            var checkpointCoordinator = new BrowserSessionCheckpointCoordinator(
                profileRegistry, loginCoordinator, null, processController, tabPool, () => new TiToAIDbContext(dbOptions));
            var gatekeeper = new HostBrowserGatekeeper(profileRegistry, loginCoordinator, tabPool, processController, checkpointCoordinator);

            await processController.LaunchPersistentContextAsync(museProfile, isHeadless: true);
            await processController.LaunchPersistentContextAsync(chatProfile, isHeadless: true);

            Assert(gatekeeper.IsProfileIdle(museProfile),
                "Job MuseAI AwaitingUser đã gửi không được giữ browser: side chat đã lưu và khôi phục mở lại qua lease.");
            Assert(!gatekeeper.IsProfileIdle(chatProfile),
                "Job ChatGPT AwaitingUser đã gửi vẫn phải giữ browser cho đến khi đối soát.");

            gatekeeper.CloseProfileIfIdle(museProfile);
            for (int wait = 0; wait < 100 && processController.IsRunning(museProfile); wait++)
            {
                await Task.Delay(20);
            }
            Assert(!processController.IsRunning(museProfile), "Profile MuseAI chỉ còn job chờ đối soát thì browser phải đóng.");
            Assert(processController.IsRunning(chatProfile), "Profile ChatGPT còn job chờ đối soát thì browser không được đóng.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>Executor theo kịch bản: job đầu chạy theo kịch bản, các job sau thành công ngay.</summary>
    private sealed class ScriptedRenderExecutor : IJobExecutor
    {
        private readonly string _firstJobId;
        private readonly Func<Func<Task>, Task<JobExecutionResult>> _firstScript;

        public ScriptedRenderExecutor(string firstJobId, Func<Func<Task>, Task<JobExecutionResult>> firstScript)
        {
            _firstJobId = firstJobId;
            _firstScript = firstScript;
        }

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JobExecutionResult> ExecuteAsync(
            JobExecutionContext context,
            Func<Task> onPreSideEffectCommitAsync,
            Func<int, string, Task>? onProgressAsync,
            CancellationToken cancellationToken)
        {
            if (context.JobId == _firstJobId)
            {
                var result = await _firstScript(onPreSideEffectCommitAsync);
                await Release.Task.WaitAsync(cancellationToken);
                return result;
            }

            return new JobExecutionResult(true, ResultReference: "asset-next", SideEffectSubmitted: true);
        }

    }

    /// <summary>
    /// Kịch bản 3: Dữ liệu cũ có MaxJobs khác MaxTabs (ví dụ MaxJobs = 1, MaxTabs = 3).
    /// Hệ thống phải lấy MaxTabs (3) làm N mới, KHÔNG lấy min(1, 3) và không bị MaxJobs cũ chặn ngầm ở 1 job!
    /// </summary>
    public static async Task Test_AccountConcurrency_LegacyAccount_UsesMaxTabsAsCapAsync()
    {
        var (dbOptions, tempDir, _, settingsService) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");

            // Seed tài khoản dữ liệu cũ: MaxConcurrentJobs = 1, MaxTabs = 3
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "profile_legacy_diff",
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "Legacy Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc-legacy-diff",
                    ProviderKey = "chatgpt-web",
                    ProfileId = "profile_legacy_diff",
                    DisplayName = "Legacy Account Different Limits",
                    VerifiedIdentity = "legacy@chatgpt.internal",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 1, // Dữ liệu cũ: MaxJobs = 1
                    MaxTabs = 3,           // Dữ liệu cũ: MaxTabs = 3
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.AppSettings.Add(new AppSetting
                {
                    Key = SettingsService.KeyScriptProvider,
                    Value = "chatgpt-web",
                    UpdatedAtUtc = now
                });

                for (int i = 1; i <= 3; i++)
                {
                    db.Projects.Add(new Project
                    {
                        Id = $"prj_legacy_{i}",
                        Name = $"Dự án {i}",
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

            // Enqueue 3 jobs
            for (int i = 1; i <= 3; i++)
            {
                await jobService.EnqueueAsync(new JobEnqueueRequest(
                    ProjectId: $"prj_legacy_{i}",
                    SceneId: null,
                    TaskType: JobTaskType.Script,
                    CustomPromptText: $"Kịch bản legacy {i}"
                ));
            }

            // Dispatch: Nếu theo logic cũ Math.Min(1, 3) = 1 thì chỉ 1 job được chạy.
            // Nhưng theo quy tắc mới: N lấy từ MaxTabs = 3 -> Cả 3 job phải được chạy đồng thời!
            int dispatched = await scheduler.DispatchPendingJobsAsync();
            Assert(dispatched == 3, $"Kỳ vọng cả 3 jobs được dispatch theo MaxTabs=3, thực tế: {dispatched}");

            // Chờ các worker chuyển trạng thái sang Running
            for (int wait = 0; wait < 100; wait++)
            {
                await using var db = new TiToAIDbContext(dbOptions);
                var runningCount = await db.Jobs.Where(j => j.State == JobState.Running).CountAsync();
                if (runningCount == 3) break;
                await Task.Delay(20);
            }

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var running = await db.Jobs.Where(j => j.State == JobState.Running).CountAsync();
                Assert(running == 3, $"Cả 3 jobs phải đang Running đồng thời, thực tế {running}");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 4: Giảm N khi đang có job chạy:
    /// - Không hủy các job đang chạy.
    /// - Slot occupancy của các job đang chạy được bảo toàn.
    /// - Ngừng nhận thêm job mới cho đến khi số job chạy giảm xuống dưới N mới.
    /// </summary>
    public static async Task Test_AccountConcurrency_ReduceNDuringRunningJobsAsync()
    {
        var (dbOptions, tempDir, _, settingsService) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");

            // Tài khoản ban đầu có N = 3
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "profile_reduce_n",
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "Profile Reduce N",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc-reduce-n",
                    ProviderKey = "chatgpt-web",
                    ProfileId = "profile_reduce_n",
                    DisplayName = "Account Reduce N",
                    VerifiedIdentity = "team@chatgpt.internal",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 3,
                    MaxTabs = 3,
                    Revision = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.AppSettings.Add(new AppSetting
                {
                    Key = SettingsService.KeyScriptProvider,
                    Value = "chatgpt-web",
                    UpdatedAtUtc = now
                });

                for (int i = 1; i <= 3; i++)
                {
                    db.Projects.Add(new Project
                    {
                        Id = $"prj_red_{i}",
                        Name = $"Dự án {i}",
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

            // Enqueue 2 jobs ban đầu
            var j1 = await jobService.EnqueueAsync(new JobEnqueueRequest($"prj_red_1", null, JobTaskType.Script, CustomPromptText: "Task 1"));
            var j2 = await jobService.EnqueueAsync(new JobEnqueueRequest($"prj_red_2", null, JobTaskType.Script, CustomPromptText: "Task 2"));

            // Dispatch: cả 2 jobs chạy
            int d1 = await scheduler.DispatchPendingJobsAsync();
            Assert(d1 == 2, "Cả 2 jobs ban đầu phải chạy");

            // Chờ worker chuyển trạng thái sang Running
            for (int wait = 0; wait < 100; wait++)
            {
                await using var db = new TiToAIDbContext(dbOptions);
                var runningCount = await db.Jobs.Where(j => j.State == JobState.Running).CountAsync();
                if (runningCount == 2) break;
                await Task.Delay(20);
            }

            // Giảm N từ 3 xuống 1 qua ProviderAccountService
            var accountService = new ProviderAccountService(dbOptions, new FakeProfileIdentityResolver());
            await accountService.UpdateAccountAsync(new UpdateProviderAccountRequest(
                Id: "acc-reduce-n",
                DisplayName: "Account Reduce N",
                Enabled: true,
                MaxTabs: 1, // Giảm N xuống 1
                MaxConcurrentJobs: 1,
                PerTaskLimitsJson: null,
                ExpectedRevision: 1
            ));

            // Kiểm tra: 2 job đang chạy KHÔNG bị hủy, slot occupancy giữ nguyên
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var running = await db.Jobs.Where(j => j.State == JobState.Running).ToListAsync();
                Assert(running.Count == 2, $"Giảm N không được hủy job đang chạy; phải còn 2 running, thực tế {running.Count}");

                var occs = await db.ActiveSlotOccupancies.CountAsync(o => o.ProviderAccountId == "acc-reduce-n");
                Assert(occs == 2, $"Slot occupancy phải giữ nguyên 2, thực tế {occs}");
            }

            // Enqueue job thứ 3
            var j3 = await jobService.EnqueueAsync(new JobEnqueueRequest($"prj_red_3", null, JobTaskType.Script, CustomPromptText: "Task 3"));

            // Thử dispatch: Không được nhận thêm vì số đang chạy (2) >= N mới (1)
            int d2 = await scheduler.DispatchPendingJobsAsync();
            Assert(d2 == 0, $"Không được nhận thêm job khi số đang chạy >= N mới; dispatched: {d2}");

            // Hoàn tất 1 job (j1): còn 1 job đang chạy
            controlledExecutor.CompleteJob(j1.JobId);
            for (int wait = 0; wait < 50; wait++)
            {
                await Task.Delay(20);
                await using var db = new TiToAIDbContext(dbOptions);
                var job = await db.Jobs.FirstAsync(x => x.Id == j1.JobId);
                if (job.State == JobState.Succeeded) break;
            }

            // Dispatch lại: Vẫn không nhận thêm vì 1 >= 1
            int d3 = await scheduler.DispatchPendingJobsAsync();
            Assert(d3 == 0, $"Còn 1 job đang chạy = N mới (1), vẫn chưa được nhận thêm; dispatched: {d3}");

            // Hoàn tất job thứ 2 (j2): số đang chạy = 0 < N mới (1)
            controlledExecutor.CompleteJob(j2.JobId);
            for (int wait = 0; wait < 50; wait++)
            {
                await Task.Delay(20);
                await using var db = new TiToAIDbContext(dbOptions);
                var job = await db.Jobs.FirstAsync(x => x.Id == j2.JobId);
                if (job.State == JobState.Succeeded) break;
            }

            // Dispatch lần này: số đang chạy = 0 < 1 -> job 3 được cấp slot chạy!
            int d4 = await scheduler.DispatchPendingJobsAsync();
            Assert(d4 == 1, $"Job 3 phải được cấp slot khi số đang chạy < N mới; dispatched: {d4}");

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                var j3Db = await db.Jobs.FirstAsync(x => x.Id == j3.JobId);
                Assert(j3Db.State == JobState.Running, "Job 3 phải chuyển sang Running");
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 5: Vòng đời browser:
    /// - Mở app không có browser automation chạy.
    /// - Có job đủ điều kiện reserve -> browser automation mở chạy ẩn.
    /// - Hết job -> browser đóng và giải phóng tài nguyên.
    /// - Chạy job tiếp theo -> browser mở lại và vẫn dùng được phiên đã lưu.
    /// </summary>
    public static async Task Test_BrowserLifecycle_FullLifecycleWithSavedSessionAsync()
    {
        var (dbOptions, tempDir, _, settingsService) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            string profileId = "profile_lifecycle_test";

            // Tạo profile và account
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = profileId,
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "Lifecycle Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Id = "acc-lifecycle",
                    ProviderKey = "chatgpt-web",
                    ProfileId = profileId,
                    DisplayName = "Lifecycle Account",
                    VerifiedIdentity = "team@chatgpt.internal",
                    IdentityVerifiedAtUtc = now,
                    Enabled = true,
                    Lifecycle = "Active",
                    MaxConcurrentJobs = 2,
                    MaxTabs = 2,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.AppSettings.Add(new AppSetting
                {
                    Key = SettingsService.KeyScriptProvider,
                    Value = "chatgpt-web",
                    UpdatedAtUtc = now
                });

                db.Projects.Add(new Project { Id = "prj_lc", Name = "Dự án LC", Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now });

                await db.SaveChangesAsync();
            }

            var profileRegistry = new ProfileRegistry(() => new TiToAIDbContext(dbOptions), tempDir);
            var profileOwner = new ProfileOwner(pid => Path.Combine(tempDir, "profiles", pid));
            var fakeLauncher = new TrackingPersistentLauncher();
            var processController = new PlaywrightBrowserController(
                profileRegistry,
                profileOwner,
                customPersistentLauncher: fakeLauncher.LaunchAsync);

            // 1. Kiểm tra: Khi mở app / load account, KHÔNG có browser automation nào chạy!
            Assert(!processController.IsRunning(profileId), "Mở app: tuyệt đối không tự khởi chạy browser.");

            var tabPool = new TabPool();
            var loginCoordinator = new LoginCoordinator(profileRegistry, processController);
            var gatekeeper = new HostBrowserGatekeeper(profileRegistry, loginCoordinator, tabPool, processController);

            var snapshotFactory = new JobSnapshotFactory(settingsService);
            var reservation = new JobReservation(dbOptions);
            var controlledExecutor = new ControlledJobExecutor();
            var scheduler = new JobScheduler(dbOptions, reservation, controlledExecutor, gatekeeper);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);

            // 2. Enqueue Job 1
            var j1 = await jobService.EnqueueAsync(new JobEnqueueRequest("prj_lc", null, JobTaskType.Script, CustomPromptText: "Task LC 1"));

            // Dispatch Job 1: Reserve thành công -> Browser automation mở chạy ẩn!
            int d1 = await scheduler.DispatchPendingJobsAsync();
            Assert(d1 == 1, "Job 1 phải được dispatch");

            for (int wait = 0; wait < 100; wait++)
            {
                if (processController.IsRunning(profileId) && tabPool.GetActiveJobCount(profileId) == 1) break;
                await Task.Delay(20);
            }

            Assert(processController.IsRunning(profileId), "Khi có job chạy: browser automation phải mở.");
            // ChatGPT Script đang bật thử nghiệm nên browser hiện để quan sát; khi tắt thử nghiệm phải chạy ẩn.
            Assert(fakeLauncher.LastLaunchWasHeadless == !ChatGptTrialMode.Enabled,
                ChatGptTrialMode.Enabled ? "ChatGPT thử nghiệm phải hiện browser (isHeadless = false)." : "Browser automation phải chạy ẩn (isHeadless = true).");
            Assert(tabPool.GetActiveJobCount(profileId) == 1, "Tab pool phải có 1 tab đang mở cho Job 1.");

            // 3. Hoàn tất Job 1
            controlledExecutor.CompleteJob(j1.JobId);
            for (int wait = 0; wait < 50; wait++)
            {
                await Task.Delay(20);
                await using var db = new TiToAIDbContext(dbOptions);
                var j = await db.Jobs.FirstAsync(x => x.Id == j1.JobId);
                if (j.State == JobState.Succeeded) break;
            }

            // Chờ browser context đóng khi idle
            for (int wait = 0; wait < 50; wait++)
            {
                if (!processController.IsRunning(profileId)) break;
                await Task.Delay(20);
            }

            Assert(!processController.IsRunning(profileId), "Khi hết job: browser phải đóng và giải phóng tài nguyên, không giữ chạy nền.");

            // Kiểm tra trạng thái phiên trong ProfileRegistry: Đóng browser không làm mất phiên đăng nhập!
            var pState = await profileRegistry.GetProfileAsync(profileId);
            Assert(pState != null && pState.SessionStatus == "Connected", "Đóng browser phải bảo toàn phiên đăng nhập (SessionStatus vẫn là Connected).");

            // 4. Enqueue Job 2 tiếp theo: Browser automation mở lại và tiếp tục dùng phiên đã lưu
            var j2 = await jobService.EnqueueAsync(new JobEnqueueRequest("prj_lc", null, JobTaskType.Script, CustomPromptText: "Task LC 2"));
            int d2 = await scheduler.DispatchPendingJobsAsync();
            Assert(d2 == 1, "Job 2 phải được dispatch");

            for (int wait = 0; wait < 100; wait++)
            {
                if (processController.IsRunning(profileId)) break;
                await Task.Delay(20);
            }

            Assert(processController.IsRunning(profileId), "Job tiếp theo: browser mở lại thành công.");
            Assert(fakeLauncher.LaunchCount == 2, "Đã khởi chạy browser lần 2 từ cùng profile.");

            controlledExecutor.CompleteJob(j2.JobId);
            for (int wait = 0; wait < 50; wait++)
            {
                await Task.Delay(20);
                if (!processController.IsRunning(profileId)) break;
            }
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 6: An toàn khi job mới đến trong lúc browser đang đóng (Closing):
    /// Không bao giờ tạo 2 browser cùng sở hữu 1 profile.
    /// </summary>
    public static async Task Test_BrowserLifecycle_JobArrivalDuringClosing_SafeAsync()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var profileRegistry = new ProfileRegistry(() => new TiToAIDbContext(dbOptions), tempDir);
            var profileOwner = new ProfileOwner(pid => Path.Combine(tempDir, "profiles", pid));
            var fakeLauncher = new TrackingPersistentLauncher();
            var processController = new PlaywrightBrowserController(
                profileRegistry,
                profileOwner,
                customPersistentLauncher: fakeLauncher.LaunchAsync);

            string profileId = "profile_closing_safety";

            // Tạo profile trong registry
            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = profileId,
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "Closing Safety Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                    UpdatedAtUtc = DateTime.UtcNow.ToString("o")
                });
                await db.SaveChangesAsync();
            }

            // Khởi chạy browser lần 1
            await processController.LaunchPersistentContextAsync(profileId, isHeadless: true);
            Assert(processController.IsRunning(profileId), "Browser lần 1 đang chạy.");

            // Bắt đầu đóng browser và đồng thời yêu cầu mở lại
            var closeTask = processController.CloseContextAsync(profileId);
            var launchTask = processController.LaunchPersistentContextAsync(profileId, isHeadless: true);

            await Task.WhenAll(closeTask, launchTask);

            // Xác minh: Luôn giải quyết xác định và an toàn, không có lỗi xung đột owner lock
            Assert(processController.IsRunning(profileId), "Browser sau đó mở lại an toàn.");
            Assert(fakeLauncher.LaunchCount == 2, "Đã launch đúng 2 lần tuần tự.");

            await processController.CloseContextAsync(profileId);
            Assert(!processController.IsRunning(profileId), "Browser đã đóng an toàn.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 7: Không đóng browser khi còn job đang chạy hoặc job chưa rõ kết quả đối soát:
    /// Job AwaitingUser có SideEffectSubmitted == 1 ngăn cản đóng browser.
    /// </summary>
    public static async Task Test_BrowserLifecycle_DoesNotCloseWhenJobActiveOrUncertainAsync()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var profileRegistry = new ProfileRegistry(() => new TiToAIDbContext(dbOptions), tempDir);
            var profileOwner = new ProfileOwner(pid => Path.Combine(tempDir, "profiles", pid));
            var fakeLauncher = new TrackingPersistentLauncher();
            var processController = new PlaywrightBrowserController(
                profileRegistry,
                profileOwner,
                customPersistentLauncher: fakeLauncher.LaunchAsync);

            string profileId = "profile_uncertain_keep";

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = profileId,
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "Uncertain Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                    UpdatedAtUtc = DateTime.UtcNow.ToString("o")
                });
                await db.SaveChangesAsync();
            }

            var tabPool = new TabPool();
            var loginCoordinator = new LoginCoordinator(profileRegistry, processController);
            var checkpointCoordinator = new BrowserSessionCheckpointCoordinator(
                profileRegistry,
                loginCoordinator,
                null,
                processController,
                tabPool,
                () => new TiToAIDbContext(dbOptions));

            var gatekeeper = new HostBrowserGatekeeper(
                profileRegistry,
                loginCoordinator,
                tabPool,
                processController,
                checkpointCoordinator);

            // Mở browser context
            await processController.LaunchPersistentContextAsync(profileId, isHeadless: true);

            // Trường hợp 1: Có job đang theo dõi với SideEffectSubmitted == 1 ở trạng thái AwaitingUser
            checkpointCoordinator.TrackJob(profileId, "job_uncertain_1", sideEffectSubmitted: true, state: JobState.AwaitingUser);

            Assert(!gatekeeper.IsProfileIdle(profileId), "Job AwaitingUser với side effect chưa đối soát -> Profile không được coi là idle.");

            gatekeeper.CloseProfileIfIdle(profileId);
            await Task.Delay(50);
            Assert(processController.IsRunning(profileId), "Tuyệt đối không được đóng browser khi có job uncertain cần đối soát.");

            // Trường hợp 2: Sau khi job đối soát xong (untrack)
            checkpointCoordinator.UntrackJob(profileId, "job_uncertain_1");
            Assert(gatekeeper.IsProfileIdle(profileId), "Khi không còn job nào -> Profile trở thành idle.");

            gatekeeper.CloseProfileIfIdle(profileId);
            for (int wait = 0; wait < 50; wait++)
            {
                if (!processController.IsRunning(profileId)) break;
                await Task.Delay(20);
            }

            Assert(!processController.IsRunning(profileId), "Khi thực sự idle: browser được đóng an toàn.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Kịch bản 8: Nhiều job cùng profile xin lease đồng thời (scheduler cấp nhiều VideoPrompt cùng lúc).
    /// Chỉ một lần khởi động browser; job đến sau không được thất bại vì "tiến trình đang hoạt động".
    /// </summary>
    public static async Task Test_BrowserLaunch_ConcurrentLeasesOnSameProfile_LaunchOnceAsync()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            string profileId = "profile_concurrent_launch";

            await using (var db = new TiToAIDbContext(dbOptions))
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = profileId,
                    Kind = "SharedAI",
                    ProviderKey = "chatgpt-web",
                    DisplayName = "Concurrent Launch Profile",
                    SessionStatus = "Connected",
                    OperationalState = "Stopped",
                    VisibilityState = "Hidden",
                    Lifecycle = "Active",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            var profileRegistry = new ProfileRegistry(() => new TiToAIDbContext(dbOptions), tempDir);
            var profileOwner = new ProfileOwner(pid => Path.Combine(tempDir, "profiles", pid));
            var fakeLauncher = new TrackingPersistentLauncher();
            // Khởi động chậm để hai job cùng đi qua bước mở browser trước khi browser xuất hiện.
            var processController = new PlaywrightBrowserController(
                profileRegistry,
                profileOwner,
                customPersistentLauncher: async (userDataDir, options) =>
                {
                    await Task.Delay(200);
                    return await fakeLauncher.LaunchAsync(userDataDir, options);
                });
            var tabPool = new TabPool();
            var loginCoordinator = new LoginCoordinator(profileRegistry, processController);
            var gatekeeper = new HostBrowserGatekeeper(profileRegistry, loginCoordinator, tabPool, processController);

            var leases = await Task.WhenAll(
                gatekeeper.TryAcquireTabLeaseAsync(profileId, JobTaskType.Script, "job-concurrent-1"),
                gatekeeper.TryAcquireTabLeaseAsync(profileId, JobTaskType.VideoPrompt, "job-concurrent-2"));

            Assert(leases.All(lease => lease is not null), "Hai job cùng profile phải nhận được tab lease.");
            Assert(fakeLauncher.LaunchCount == 1, "Browser của profile chỉ được khởi động một lần.");

            foreach (var lease in leases) lease?.Dispose();
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    private sealed class TrackingPersistentLauncher
    {
        public int LaunchCount { get; private set; }
        public bool? LastLaunchWasHeadless { get; private set; }

        public Task<IBrowserContext> LaunchAsync(string userDataDir, BrowserTypeLaunchPersistentContextOptions options)
        {
            LaunchCount++;
            LastLaunchWasHeadless = options.Headless;
            var proxy = DispatchProxy.Create<IBrowserContext, BrowserSmokeTests.MockBrowserContextProxy>();
            return Task.FromResult(proxy);
        }
    }

    private sealed class FakeProfileIdentityResolver : IProfileIdentityResolver
    {
        private readonly string? _identity;
        public FakeProfileIdentityResolver(string? identity = null) => _identity = identity;

        public Task<ProfileIdentityResolutionResult> ResolveProfileIdentityAsync(string profileId, string provider, CancellationToken cancellationToken = default)
        {
            if (_identity != null)
            {
                return Task.FromResult(ProfileIdentityResolutionResult.Success(profileId, provider, _identity, "Connected"));
            }
            return Task.FromResult(ProfileIdentityResolutionResult.Failed("Chưa xác minh danh tính."));
        }
    }

    private sealed class ControlledJobExecutor : IJobExecutor
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _tcsMap = new();

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
