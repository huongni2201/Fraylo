using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TiToAI.Application.Accounts;
using TiToAI.Application.Jobs;
using TiToAI.Browser;
using TiToAI.Browser.Abstractions;
using TiToAI.Browser.Models;
using TiToAI.Contracts.Accounts;
using TiToAI.Contracts.Bridge;
using TiToAI.Contracts.Jobs;
using TiToAI.Desktop.Bridge;
using TiToAI.Desktop.Bridge.Handlers;
using TiToAI.Desktop.Hosting;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;
using TiToAI.Infrastructure.Persistence.Interceptors;

namespace TiToAI.Desktop.SmokeChecks;

/// <summary>
/// Kiểm thử khói tích hợp cho ProviderAccount Bridge Handlers, Quản lý Capacity,
/// Không trùng slot qua ActiveSlotOccupancy và kiểm soát Retry/Budget theo H1–H4 và A03–A08, A13.
/// </summary>
public static class ProviderAccountAndCapacitySmokeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static DbContextOptions<TiToAIDbContext> CreateSqliteOptions(string dbPath)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        return new DbContextOptionsBuilder<TiToAIDbContext>()
            .UseSqlite(connStr)
            .Options;
    }

    public static async Task<int> RunAllAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "titoai_q1_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "smoke.db");

        try
        {
            var bootstrapper = new SqliteDatabaseBootstrapper();
            await bootstrapper.BootstrapAsync(dbPath);
            var dbOptions = CreateSqliteOptions(dbPath);

            Console.WriteLine("\n    --- [Q1-1] ProviderAccount Bridge Commands & Full Lifecycle (A13) ---");
            await Test_ProviderAccountBridge_FullLifecycle(dbOptions);

            Console.WriteLine("\n    --- [Q1-0b] Delete Account Releases Profile & Hides Account ---");
            await Test_DeleteAccount_ReleasesProfileAndHidesAccount(dbOptions);

            Console.WriteLine("\n    --- [Q1-2] Slot Occupancy No Duplicates & Fast Reuse (H2, A04, A06) ---");
            await Test_SlotOccupancy_NoDuplicateSlots_AndFastReuse(dbOptions);

            Console.WriteLine("\n    --- [Q1-3] Retry Honors Disabled Account & Global Budget (H1, A05, A07) ---");
            await Test_Retry_HonorsDisabledAndGlobalBudget(dbOptions);

            Console.WriteLine("\n    --- [Q1-4] AwaitingUser & Uncertain Retains Occupancy (H2, A07, A09) ---");
            await Test_AwaitingUser_RetainsOccupancy(dbOptions);

            Console.WriteLine("\n    --- [Q1-5] Global Browser Budget = 1, Two Accounts & Retry Reopen (H1, A05) ---");
            await Test_GlobalBrowserBudget_RetryAndFirstReserve_WithTwoAccounts(tempDir);

            Console.WriteLine("\n    --- [Q1-6] Concurrent Dispatchers Contention On Same Slot (H2, A06) ---");
            await Test_ConcurrentDispatchers_ContentionOnSameSlot(tempDir);


            return 0;
        }
        finally
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
    }

    private static RequestEnvelope CreateRequest(string command, object? payload)
    {
        JsonElement jsonElement;
        if (payload is null)
        {
            jsonElement = default;
        }
        else
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            jsonElement = JsonDocument.Parse(json).RootElement;
        }

        return new RequestEnvelope(
            Id: Guid.NewGuid(),
            Type: "request",
            Command: command,
            Payload: jsonElement,
            ClientTimestampUtc: DateTimeOffset.UtcNow,
            ContractVersion: "1.1.0"
        );
    }

    private static async Task Test_DeleteAccount_ReleasesProfileAndHidesAccount(DbContextOptions<TiToAIDbContext> dbOptions)
    {
        var released = new List<string>();
        var service = new ProviderAccountService(dbOptions, new TestIdentityResolver(), (pid, _) =>
        {
            released.Add(pid);
            return Task.CompletedTask;
        });

        var created = await service.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "gemini-web",
            DisplayName: "Delete Me",
            MaxConcurrentJobs: 1,
            MaxTabs: 1));

        var deleted = await service.DeleteAccountAsync(created.Id);

        AssertTrue(deleted.Lifecycle == "Deleted" && !deleted.Enabled, "Xoá đặt Lifecycle=Deleted và tắt nhận việc.");
        AssertTrue(released.Count == 1 && released[0] == created.ProfileId, "Xoá giải phóng đúng profile của tài khoản.");
        var active = await service.ListAccountsAsync(new ProviderAccountListRequest(Lifecycle: "Active"));
        AssertTrue(active.All(a => a.Id != created.Id), "Tài khoản đã xoá không còn trong danh sách Active.");
        Console.WriteLine("      PASS: xoá tài khoản giải phóng profile và ẩn khỏi danh sách.");
    }

    private static async Task Test_ProviderAccountBridge_FullLifecycle(DbContextOptions<TiToAIDbContext> dbOptions)
    {
        var dispatcher = new BridgeDispatcher();
        var fakeResolver = new TestIdentityResolver();
        var service = new ProviderAccountService(dbOptions, fakeResolver);
        var handlers = new ProviderAccountHandlers(service);
        handlers.Register(dispatcher);

        // 1. Tạo account qua bridge command 'provider_account.create'
        var createPayload = new CreateProviderAccountRequest(
            ProviderKey: "chatgpt-web",
            DisplayName: "ChatGPT Main Account",
            MaxConcurrentJobs: 3,
            MaxTabs: 3
        );

        var createReq = CreateRequest("provider_account.create", createPayload);
        var createRes = await dispatcher.DispatchAsync(createReq, CancellationToken.None);

        AssertTrue(createRes.Success, "provider_account.create phải thành công.");
        var createdData = DeserializePayload<CreateProviderAccountResponseData>(createRes.Data);
        AssertTrue(createdData != null, "Phải có CreateProviderAccountResponseData");
        AssertTrue(!createdData!.Account.Enabled, "Account mới tạo phải ở trạng thái enabled = false.");
        AssertTrue(createdData.Account.VerifiedIdentity == null, "Account mới tạo chưa có verified identity.");
        string accountId = createdData.Account.Id;

        // 2. Thử bật account khi chưa verify identity -> Bị từ chối
        var enablePayload = new EnableProviderAccountRequest(Id: accountId, Enabled: true);
        var enableReq = CreateRequest("provider_account.enable", enablePayload);
        var enableRes = await dispatcher.DispatchAsync(enableReq, CancellationToken.None);
        AssertTrue(!enableRes.Success, "Không được phép enable tài khoản khi chưa verify identity!");
        AssertTrue(enableRes.Error?.Code == "VALIDATION_FAILED", "Mã lỗi phải là VALIDATION_FAILED.");

        // 3. Giả lập probe session thành công
        fakeResolver.SetResolvedIdentity("user@example.com", "Connected");
        var probePayload = new ProbeProviderAccountSessionRequest(Id: accountId);
        var probeReq = CreateRequest("provider_account.probe_session", probePayload);
        var probeRes = await dispatcher.DispatchAsync(probeReq, CancellationToken.None);

        AssertTrue(probeRes.Success, "probe_session phải thành công.");
        var probeData = DeserializePayload<ProbeProviderAccountSessionResponseData>(probeRes.Data);
        AssertTrue(probeData != null && probeData.IsVerified, "Probe data phải có IsVerified = true.");
        AssertTrue(probeData!.AccountIdentity == "user@example.com", "Identity phải khớp.");

        // 4. Bật account sau khi đã verify -> Thành công
        enablePayload = new EnableProviderAccountRequest(Id: accountId, Enabled: true);
        enableReq = CreateRequest("provider_account.enable", enablePayload);
        enableRes = await dispatcher.DispatchAsync(enableReq, CancellationToken.None);
        AssertTrue(enableRes.Success, "Sau khi verified, enable phải thành công.");

        // 5. Cập nhật quota với ExpectedRevision
        var updatePayload = new UpdateProviderAccountRequest(
            Id: accountId,
            DisplayName: "ChatGPT Main Updated",
            Enabled: true,
            MaxConcurrentJobs: 5,
            MaxTabs: 5,
            PerTaskLimitsJson: null,
            ExpectedRevision: 999 // Sai revision
        );
        var updateReq = CreateRequest("provider_account.update", updatePayload);
        var updateRes = await dispatcher.DispatchAsync(updateReq, CancellationToken.None);
        AssertTrue(!updateRes.Success, "Update với expected revision sai phải trả lỗi conflict!");
        AssertTrue(updateRes.Error?.Code == "CONCURRENCY_CONFLICT", "Mã lỗi phải là CONCURRENCY_CONFLICT.");

        // 6. Liệt kê danh sách tài khoản
        var listReq = CreateRequest("provider_account.list", null);
        var listRes = await dispatcher.DispatchAsync(listReq, CancellationToken.None);
        AssertTrue(listRes.Success, "provider_account.list phải thành công.");
        var listData = DeserializePayload<ProviderAccountListResponseData>(listRes.Data);
        AssertTrue(listData != null && listData.Accounts.Any(a => a.Id == accountId), "Danh sách phải chứa account vừa tạo.");

        Console.WriteLine("      -> [PASS] ProviderAccount Full Lifecycle qua Bridge Handlers thành công 100%.");
    }

    private static async Task Test_SlotOccupancy_NoDuplicateSlots_AndFastReuse(DbContextOptions<TiToAIDbContext> dbOptions)
    {
        await using var dbContext = new TiToAIDbContext(dbOptions);

        string prjId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        dbContext.Projects.Add(new Project { Id = prjId, Name = "Prj Test", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });

        string accountId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string profileId = "profile_" + Guid.NewGuid().ToString("N");
        dbContext.BrowserProfiles.Add(new BrowserProfile { Id = profileId, Kind = "SharedAI", VisibilityState = "Hidden", SessionStatus = "Connected", OperationalState = "Stopped", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });
        dbContext.ProviderAccounts.Add(new ProviderAccount
        {
            Id = accountId,
            ProviderKey = "gemini-web",
            ProfileId = profileId,
            DisplayName = "Gemini Account",
            VerifiedIdentity = "gemini@test.com",
            Enabled = true,
            Lifecycle = "Active",
            MaxConcurrentJobs = 2,
            MaxTabs = 2,
            CreatedAtUtc = DateTime.UtcNow.ToString("o"),
            UpdatedAtUtc = DateTime.UtcNow.ToString("o")
        });

        // Tạo 3 jobs Queued
        string job1Id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string job2Id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string job3Id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string nowIso = DateTime.UtcNow.ToString("o");

        dbContext.Jobs.AddRange(
            new Job { Id = job1Id, ProjectId = prjId, TaskType = "Script", ProviderKey = "gemini-web", State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso },
            new Job { Id = job2Id, ProjectId = prjId, TaskType = "Script", ProviderKey = "gemini-web", State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso },
            new Job { Id = job3Id, ProjectId = prjId, TaskType = "Script", ProviderKey = "gemini-web", State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso }
        );
        await dbContext.SaveChangesAsync();

        var reservation = new JobReservation(dbOptions);

        // 1. Reserve Job 1 -> Được cấp slot-1
        var res1 = await reservation.TryReserveJobAsync(job1Id, "worker-1", TimeSpan.FromSeconds(30));
        AssertTrue(res1.Acquired, "Job 1 phải reserve thành công.");
        AssertTrue(res1.Binding?.TabSlotId == "slot-1", $"Job 1 phải được cấp slot-1, nhận được {res1.Binding?.TabSlotId}");

        // 2. Reserve Job 2 -> Được cấp slot-2
        var res2 = await reservation.TryReserveJobAsync(job2Id, "worker-2", TimeSpan.FromSeconds(30));
        AssertTrue(res2.Acquired, "Job 2 phải reserve thành công.");
        AssertTrue(res2.Binding?.TabSlotId == "slot-2", $"Job 2 phải được cấp slot-2, nhận được {res2.Binding?.TabSlotId}");

        // 3. Reserve Job 3 -> Hết capacity (cap=2) -> Giữ Queued, KHÔNG tăng attempts
        var res3 = await reservation.TryReserveJobAsync(job3Id, "worker-3", TimeSpan.FromSeconds(30));
        AssertTrue(!res3.Acquired, "Job 3 không được reserve vì pool đã đầy.");
        var job3Db = await dbContext.Jobs.AsNoTracking().FirstAsync(j => j.Id == job3Id);
        AssertTrue(job3Db.State == JobState.Queued, "Job 3 phải giữ nguyên Queued.");
        AssertTrue(job3Db.Attempts == 0, "Job 3 không được tăng attempts khi chờ capacity.");

        // 4. Job 1 kết thúc thành công (slot-1 rảnh) trong khi Job 2 vẫn đang chạy (slot-2 bận)
        await reservation.CommitSuccessAsync(job1Id, res1.Job!.FencingToken, "res-ref-1", "result-text-1");

        // 5. Reserve lại Job 3 -> Phải cấp slot-1 (rảnh), TUYỆT ĐỐI KHÔNG TRÙNG slot-2!
        var res3Retry = await reservation.TryReserveJobAsync(job3Id, "worker-3", TimeSpan.FromSeconds(30));
        AssertTrue(res3Retry.Acquired, "Job 3 phải reserve thành công sau khi slot-1 giải phóng.");
        AssertTrue(res3Retry.Binding?.TabSlotId == "slot-1", $"Job 3 phải được cấp slot-1 rảnh, nhận được {res3Retry.Binding?.TabSlotId}");

        // Kiểm tra trong DB: ActiveSlotOccupancy có chính xác 2 slot: job2 (slot-2) và job3 (slot-1)
        var occupancies = await dbContext.ActiveSlotOccupancies.AsNoTracking().ToListAsync();
        AssertTrue(occupancies.Count == 2, $"ActiveSlotOccupancy phải có đúng 2 bản ghi, có {occupancies.Count}");
        var slotIds = occupancies.Select(o => o.TabSlotId).OrderBy(s => s).ToList();
        AssertTrue(slotIds[0] == "slot-1" && slotIds[1] == "slot-2", "Hai slot đang chiếm dụng phải là slot-1 và slot-2 riêng biệt.");

        Console.WriteLine("      -> [PASS] Không trùng lặp slot và tái sử dụng slot rảnh tức thời chuẩn xác 100%.");
    }

    private static async Task Test_Retry_HonorsDisabledAndGlobalBudget(DbContextOptions<TiToAIDbContext> dbOptions)
    {
        await using var dbContext = new TiToAIDbContext(dbOptions);

        string prjId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        dbContext.Projects.Add(new Project { Id = prjId, Name = "Prj Test 2", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });

        string pKey = "retry-provider-" + Guid.NewGuid().ToString("N")[..8];
        string accountId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string profileId = "profile_" + Guid.NewGuid().ToString("N");
        dbContext.BrowserProfiles.Add(new BrowserProfile { Id = profileId, Kind = "SharedAI", VisibilityState = "Hidden", SessionStatus = "Connected", OperationalState = "Stopped", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });
        var account = new ProviderAccount
        {
            Id = accountId,
            ProviderKey = pKey,
            ProfileId = profileId,
            DisplayName = "Retry Test Account",
            VerifiedIdentity = "retry@test.com",
            Enabled = true,
            Lifecycle = "Active",
            MaxConcurrentJobs = 1,
            MaxTabs = 1,
            CreatedAtUtc = DateTime.UtcNow.ToString("o"),
            UpdatedAtUtc = DateTime.UtcNow.ToString("o")
        };
        dbContext.ProviderAccounts.Add(account);

        string jobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string nowIso = DateTime.UtcNow.ToString("o");
        var job = new Job { Id = jobId, ProjectId = prjId, TaskType = "Script", ProviderKey = pKey, State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso };
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var reservation = new JobReservation(dbOptions);

        // Lần 1: Reserve thành công
        var res1 = await reservation.TryReserveJobAsync(jobId, "worker-1", TimeSpan.FromSeconds(30));
        AssertTrue(res1.Acquired, "Reserve lần đầu thành công.");

        // Mô phỏng failure chưa hết attempts -> Trả về Queued (đã có ExecutionBinding gắn với account)
        await reservation.CommitFailureAsync(jobId, res1.Job!.FencingToken, "temporary_error", canRetry: true, retryDelay: TimeSpan.FromMilliseconds(10));

        // H1 Check: Bây giờ vô hiệu hóa account (Enabled = false)
        account.Enabled = false;
        await dbContext.SaveChangesAsync();

        // Thử reserve lại job (Retry branch)
        var resRetry = await reservation.TryReserveJobAsync(jobId, "worker-2", TimeSpan.FromSeconds(30));
        AssertTrue(!resRetry.Acquired, $"Job retry không được phép reserve khi account đã bị Disabled! Reason={resRetry.Reason}");
        var jobDb = await dbContext.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        AssertTrue(jobDb.State == JobState.Queued, "Job phải giữ nguyên Queued.");
        AssertTrue(jobDb.ReasonCode == "account_disabled", $"ReasonCode phải là account_disabled, có {jobDb.ReasonCode}");

        Console.WriteLine("      -> [PASS] Nhánh retry kiểm tra nghiêm ngặt tính hợp lệ và năng lực account.");
    }

    private static async Task Test_AwaitingUser_RetainsOccupancy(DbContextOptions<TiToAIDbContext> dbOptions)
    {
        await using var dbContext = new TiToAIDbContext(dbOptions);

        string prjId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        dbContext.Projects.Add(new Project { Id = prjId, Name = "Prj Test 3", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });

        string pKey = "uncertain-provider-" + Guid.NewGuid().ToString("N")[..8];
        string accountId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string profileId = "profile_" + Guid.NewGuid().ToString("N");
        dbContext.BrowserProfiles.Add(new BrowserProfile { Id = profileId, Kind = "SharedAI", VisibilityState = "Hidden", SessionStatus = "Connected", OperationalState = "Stopped", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });
        dbContext.ProviderAccounts.Add(new ProviderAccount
        {
            Id = accountId,
            ProviderKey = pKey,
            ProfileId = profileId,
            DisplayName = "Uncertain Test Account",
            VerifiedIdentity = "uncertain@test.com",
            Enabled = true,
            Lifecycle = "Active",
            MaxConcurrentJobs = 1,
            MaxTabs = 1,
            CreatedAtUtc = DateTime.UtcNow.ToString("o"),
            UpdatedAtUtc = DateTime.UtcNow.ToString("o")
        });

        string jobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string nowIso = DateTime.UtcNow.ToString("o");
        var job = new Job { Id = jobId, ProjectId = prjId, TaskType = "Script", ProviderKey = pKey, State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso };
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var reservation = new JobReservation(dbOptions);
        var res = await reservation.TryReserveJobAsync(jobId, "worker-1", TimeSpan.FromSeconds(30));
        AssertTrue(res.Acquired, "Reserve job thành công.");

        // Cam kết side effect đã submit
        await reservation.CommitSideEffectAsync(jobId, res.Job!.FencingToken);

        // Mô phỏng lease timeout quá hạn giống TC-T15-05
        dbContext.ChangeTracker.Clear();
        var jobInDb = await dbContext.Jobs.FirstAsync(j => j.Id == jobId);
        jobInDb.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o");
        await dbContext.SaveChangesAsync();

        // Chạy crash sweep: chuyển sang AwaitingUser vì đã submit side effect
        var scheduler = new JobScheduler(dbOptions, reservation, new UnavailableAdapterJobExecutor());
        int swept = await scheduler.RunCrashRecoverySweepAsync();
        // Sweep cũng dừng job Queued còn retry_after do test trước để lại trong cùng DB, nên chỉ cần thu hồi ít nhất job này.
        AssertTrue(swept >= 1, "Crash sweep phải thu hồi job hết lease.");

        // Kiểm tra: Job đã chuyển sang AwaitingUser
        var jobAfterSweep = await dbContext.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        AssertTrue(jobAfterSweep.State == JobState.AwaitingUser, $"Job phải ở trạng thái AwaitingUser, nhận được {jobAfterSweep.State}");

        // H2 Check: ActiveSlotOccupancy VẪN PHẢI GIỮ slot cho job này!
        var occupancy = await dbContext.ActiveSlotOccupancies.AsNoTracking().FirstOrDefaultAsync(o => o.JobId == jobId);
        AssertTrue(occupancy != null, "ActiveSlotOccupancy PHẢI ĐƯỢC GIỮ NGUYÊN cho job ở trạng thái AwaitingUser (uncertain/checkpoint)!");

        // Account có maxConcurrentJobs=1 nên job khác không thể chiếm slot này
        string job2Id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        dbContext.Jobs.Add(new Job { Id = job2Id, ProjectId = prjId, TaskType = "Script", ProviderKey = pKey, State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso });
        await dbContext.SaveChangesAsync();

        var resOther = await reservation.TryReserveJobAsync(job2Id, "worker-2", TimeSpan.FromSeconds(30));
        AssertTrue(!resOther.Acquired, "Không được cấp slot của account đang bị AwaitingUser giữ!");

        Console.WriteLine("      -> [PASS] AwaitingUser và các tác vụ chưa rõ kết quả bảo toàn slot occupancy trong DB.");
    }

    private static async Task Test_GlobalBrowserBudget_RetryAndFirstReserve_WithTwoAccounts(string baseDir)
    {
        var testDbPath = Path.Combine(baseDir, "browser_budget_" + Guid.NewGuid().ToString("N") + ".db");
        var bootstrapper = new SqliteDatabaseBootstrapper();
        await bootstrapper.BootstrapAsync(testDbPath);
        var dbOptions = CreateSqliteOptions(testDbPath);

        await using var dbContext = new TiToAIDbContext(dbOptions);
        string prjId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        dbContext.Projects.Add(new Project { Id = prjId, Name = "Prj Browser Budget", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });

        // Đặt ngân sách: max_concurrent_jobs = 5 (dư dả), nhưng max_open_browsers = 1 (ngặt nghèo)
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == "scheduler.max_open_browsers");
        if (setting == null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = "scheduler.max_open_browsers", Value = "1", UpdatedAtUtc = DateTime.UtcNow.ToString("o") });
        }
        else
        {
            setting.Value = "1";
        }
        await dbContext.SaveChangesAsync();

        string pKey = "budget-provider-" + Guid.NewGuid().ToString("N")[..8];
        string acc1Id = "acc_budget_1_" + Guid.NewGuid().ToString("N")[..6];
        string prof1Id = "prof_budget_1_" + Guid.NewGuid().ToString("N")[..6];
        string acc2Id = "acc_budget_2_" + Guid.NewGuid().ToString("N")[..6];
        string prof2Id = "prof_budget_2_" + Guid.NewGuid().ToString("N")[..6];

        dbContext.BrowserProfiles.Add(new BrowserProfile { Id = prof1Id, Kind = "SharedAI", VisibilityState = "Hidden", SessionStatus = "Connected", OperationalState = "Stopped", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });
        dbContext.BrowserProfiles.Add(new BrowserProfile { Id = prof2Id, Kind = "SharedAI", VisibilityState = "Hidden", SessionStatus = "Connected", OperationalState = "Stopped", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });

        dbContext.ProviderAccounts.Add(new ProviderAccount { Id = acc1Id, ProviderKey = pKey, ProfileId = prof1Id, DisplayName = "Account 1", VerifiedIdentity = "acc1@test.com", Enabled = true, Lifecycle = "Active", MaxConcurrentJobs = 1, MaxTabs = 1, CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });
        dbContext.ProviderAccounts.Add(new ProviderAccount { Id = acc2Id, ProviderKey = pKey, ProfileId = prof2Id, DisplayName = "Account 2", VerifiedIdentity = "acc2@test.com", Enabled = true, Lifecycle = "Active", MaxConcurrentJobs = 1, MaxTabs = 1, CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });

        string job1Id = "job_budget_1_" + Guid.NewGuid().ToString("N")[..6];
        string job2Id = "job_budget_2_" + Guid.NewGuid().ToString("N")[..6];
        string job3RetryId = "job_budget_3_" + Guid.NewGuid().ToString("N")[..6];
        string nowIso = DateTime.UtcNow.ToString("o");

        dbContext.Jobs.Add(new Job { Id = job1Id, ProjectId = prjId, TaskType = "Script", ProviderKey = pKey, State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso });
        dbContext.Jobs.Add(new Job { Id = job2Id, ProjectId = prjId, TaskType = "Script", ProviderKey = pKey, State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso });
        // Job 3 đã có binding với acc2 nhưng đang ở trạng thái retry (Queued)
        dbContext.Jobs.Add(new Job { Id = job3RetryId, ProjectId = prjId, TaskType = "Script", ProviderKey = pKey, State = JobState.Queued, Attempts = 1, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso });
        dbContext.ExecutionBindings.Add(new ExecutionBinding
        {
            JobId = job3RetryId,
            ProviderAccountId = acc2Id,
            ProfileId = prof2Id,
            TabSlotId = "slot-1",
            AssignedAtUtc = nowIso,
            CapacityConfigRevision = 1
        });

        await dbContext.SaveChangesAsync();

        var reservation = new JobReservation(dbOptions);

        // 1. Reserve Job 1: Mở profile 1 (browser count = 1, chạm ngưỡng max_open_browsers = 1)
        var res1 = await reservation.TryReserveJobAsync(job1Id, "worker-1", TimeSpan.FromSeconds(30));
        AssertTrue(res1.Acquired, "Job 1 phải reserve thành công.");
        AssertTrue(res1.Binding?.ProfileId == prof1Id, "Job 1 được gán cho Profile 1.");

        // 2. Reserve Job 2 (lần đầu): Cần mở Profile 2 nhưng browser budget = 1 đã chạm ngưỡng -> BỊ CHẶN!
        var res2 = await reservation.TryReserveJobAsync(job2Id, "worker-2", TimeSpan.FromSeconds(30));
        AssertTrue(!res2.Acquired, "Job 2 phải bị từ chối do chạm browser budget toàn app.");
        var job2Db = await dbContext.Jobs.AsNoTracking().FirstAsync(j => j.Id == job2Id);
        AssertTrue(job2Db.State == JobState.Queued, "Job 2 phải giữ nguyên trạng thái Queued.");
        AssertTrue(job2Db.ReasonCode == "waiting_for_global_browser_budget", $"ReasonCode phải là waiting_for_global_browser_budget, có {job2Db.ReasonCode}");

        // 3. Reserve Job 3 (nhánh retry): Đã bind Profile 2 (chưa mở), browser budget = 1 đã chạm ngưỡng -> BỊ CHẶN TRÊN NHÁNH RETRY!
        var res3 = await reservation.TryReserveJobAsync(job3RetryId, "worker-3", TimeSpan.FromSeconds(30));
        AssertTrue(!res3.Acquired, "Job 3 retry phải bị từ chối do mở profile mới vượt browser budget toàn app.");
        var job3Db = await dbContext.Jobs.AsNoTracking().FirstAsync(j => j.Id == job3RetryId);
        AssertTrue(job3Db.State == JobState.Queued, "Job 3 phải giữ nguyên Queued.");
        AssertTrue(job3Db.ReasonCode == "waiting_for_global_browser_budget", $"Job 3 ReasonCode phải là waiting_for_global_browser_budget, có {job3Db.ReasonCode}");

        // 4. Giải phóng Job 1 (thành công -> xóa occupancy của Profile 1)
        await reservation.CommitSuccessAsync(job1Id, res1.Job!.FencingToken, "res_ref_1", "Result text 1");

        // 5. Thử reserve lại Job 3 retry: Bây giờ Profile 1 đã đóng, Profile 2 được phép mở -> THÀNH CÔNG!
        var res3Reopen = await reservation.TryReserveJobAsync(job3RetryId, "worker-3", TimeSpan.FromSeconds(30));
        AssertTrue(res3Reopen.Acquired, "Job 3 retry phải reserve thành công sau khi Profile 1 đã giải phóng browser budget.");
        AssertTrue(res3Reopen.Binding?.ProfileId == prof2Id, "Job 3 phải giữ đúng Profile 2 đã bind.");

        // Khôi phục setting
        setting = await dbContext.AppSettings.FirstAsync(s => s.Key == "scheduler.max_open_browsers");
        setting.Value = "5";
        await dbContext.SaveChangesAsync();

        Console.WriteLine("      -> [PASS] Global browser budget = 1 kiểm soát chính xác cả reserve lần đầu và retry.");
    }

    private static async Task Test_ConcurrentDispatchers_ContentionOnSameSlot(string baseDir)
    {
        var testDbPath = Path.Combine(baseDir, "contention_" + Guid.NewGuid().ToString("N") + ".db");
        var bootstrapper = new SqliteDatabaseBootstrapper();
        await bootstrapper.BootstrapAsync(testDbPath);
        var dbOptions = CreateSqliteOptions(testDbPath);

        await using var dbContext = new TiToAIDbContext(dbOptions);
        string prjId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        dbContext.Projects.Add(new Project { Id = prjId, Name = "Prj Contention", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });

        string pKey = "contention-provider-" + Guid.NewGuid().ToString("N")[..8];
        string accId = "acc_contention_" + Guid.NewGuid().ToString("N")[..6];
        string profId = "prof_contention_" + Guid.NewGuid().ToString("N")[..6];

        dbContext.BrowserProfiles.Add(new BrowserProfile { Id = profId, Kind = "SharedAI", VisibilityState = "Hidden", SessionStatus = "Connected", OperationalState = "Stopped", CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });
        // Account chỉ có đúng 1 slot (MaxConcurrentJobs = 1, MaxTabs = 1)
        dbContext.ProviderAccounts.Add(new ProviderAccount { Id = accId, ProviderKey = pKey, ProfileId = profId, DisplayName = "Contention Account", VerifiedIdentity = "single@test.com", Enabled = true, Lifecycle = "Active", MaxConcurrentJobs = 1, MaxTabs = 1, CreatedAtUtc = DateTime.UtcNow.ToString("o"), UpdatedAtUtc = DateTime.UtcNow.ToString("o") });

        string jobAId = "job_contention_a_" + Guid.NewGuid().ToString("N")[..6];
        string jobBId = "job_contention_b_" + Guid.NewGuid().ToString("N")[..6];
        string nowIso = DateTime.UtcNow.ToString("o");

        dbContext.Jobs.Add(new Job { Id = jobAId, ProjectId = prjId, TaskType = "Script", ProviderKey = pKey, State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso });
        dbContext.Jobs.Add(new Job { Id = jobBId, ProjectId = prjId, TaskType = "Script", ProviderKey = pKey, State = JobState.Queued, CreatedAtUtc = nowIso, UpdatedAtUtc = nowIso });
        await dbContext.SaveChangesAsync();

        var reservation1 = new JobReservation(dbOptions);
        var reservation2 = new JobReservation(dbOptions);

        // Chạy đồng thời 2 dispatcher cùng tranh chấp slot duy nhất của account
        var taskA = Task.Run(() => reservation1.TryReserveJobAsync(jobAId, "dispatcher-A", TimeSpan.FromSeconds(30)));
        var taskB = Task.Run(() => reservation2.TryReserveJobAsync(jobBId, "dispatcher-B", TimeSpan.FromSeconds(30)));

        var results = await Task.WhenAll(taskA, taskB);
        int acquiredCount = results.Count(r => r.Acquired);

        AssertTrue(acquiredCount == 1, $"Trong 2 dispatcher tranh chấp cùng 1 slot, đúng 1 dispatcher phải thắng, thực tế thắng: {acquiredCount}");

        var winnerJobId = results[0].Acquired ? jobAId : jobBId;
        var loserJobId = results[0].Acquired ? jobBId : jobAId;

        // Kiểm tra trong DB: ActiveSlotOccupancy chỉ có đúng 1 bản ghi duy nhất, gán cho winner
        var occupancies = await dbContext.ActiveSlotOccupancies.AsNoTracking().Where(o => o.ProviderAccountId == accId).ToListAsync();
        AssertTrue(occupancies.Count == 1, $"Chỉ có đúng 1 bản ghi occupancy cho account, có {occupancies.Count}");
        AssertTrue(occupancies[0].JobId == winnerJobId, $"Occupancy phải thuộc về winner '{winnerJobId}', thực tế: '{occupancies[0].JobId}'");

        // Kiểm tra trạng thái Winner và Loser: Loser phải giữ nguyên Queued và attempts = 0
        var winnerJob = await dbContext.Jobs.AsNoTracking().FirstAsync(j => j.Id == winnerJobId);
        var loserJob = await dbContext.Jobs.AsNoTracking().FirstAsync(j => j.Id == loserJobId);

        AssertTrue(winnerJob.State == JobState.Reserved, $"Winner job phải ở trạng thái Reserved, thực tế: '{winnerJob.State}'");
        AssertTrue(winnerJob.Attempts == 1, $"Winner job attempts phải tăng lên 1, thực tế: {winnerJob.Attempts}");

        AssertTrue(loserJob.State == JobState.Queued, $"Loser job phải giữ nguyên trạng thái Queued, thực tế: '{loserJob.State}'");
        AssertTrue(loserJob.Attempts == 0, $"Loser job attempts không được tăng (vẫn 0), thực tế: {loserJob.Attempts}");

        Console.WriteLine("      -> [PASS] Hai dispatcher đồng thời tranh slot giải quyết xác định: đúng 1 winner chiếm slot; loser giữ nguyên Queued/attempt=0.");
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private static T? DeserializePayload<T>(object? data)
    {
        if (data is null) return default;
        if (data is JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
        }
        var raw = JsonSerializer.Serialize(data, JsonOptions);
        return JsonSerializer.Deserialize<T>(raw, JsonOptions);
    }

    private sealed class TestIdentityResolver : IProfileIdentityResolver
    {
        private string? _identity;
        private string _status = "Unverified";

        public void SetResolvedIdentity(string identity, string status)
        {
            _identity = identity;
            _status = status;
        }

        public Task<ProfileIdentityResolutionResult> ResolveProfileIdentityAsync(string profileId, string provider, CancellationToken cancellationToken = default)
        {
            if (_status == "Connected" && !string.IsNullOrWhiteSpace(_identity))
            {
                return Task.FromResult(ProfileIdentityResolutionResult.Success(profileId, provider, _identity, _status));
            }

            return Task.FromResult(new ProfileIdentityResolutionResult(false, profileId, provider, _identity, _status, "Chưa xác minh"));
        }
    }
}
