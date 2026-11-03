using System.IO;
using System.Reflection;
using System.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Fraylo.Browser;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Accounts;
using Fraylo.Contracts.Browser;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói (Smoke Tests) cho Task T13:
/// Browser Profile Registry, Single Instance Profile Lock, Tab Pool, Login Coordinator,
/// Quiescing / Drain Handoff, Crash Recovery, Session Probe, Playwright Process Controller & Login Handoff Verification.
/// </summary>
public static class BrowserSmokeTests
{
    private static async Task<(SqliteConnection Connection, Func<FrayloDbContext> Factory)> CreateInMemoryDbContextFactoryAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Default Timeout=5");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<FrayloDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;

        using (var initialDb = new FrayloDbContext(options))
        {
            await initialDb.Database.EnsureCreatedAsync();
        }

        return (connection, () => new FrayloDbContext(options));
    }

    /// <summary>
    /// Test 1: Đảm bảo đường dẫn profile an toàn, không chứa email/username và chặn directory traversal.
    /// </summary>
    public static async Task Test_ProfileRegistry_PathSafety_NoEmailOrTraversal()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_profiles_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);

                // Đường dẫn chuẩn
                string sharedDir = registry.GetUserDataDir("shared_ai", BrowserProfileKind.SharedAI);
                Assert(Path.GetFullPath(sharedDir) == Path.Combine(tempDir, "shared_ai"), "Đường dẫn Shared AI không khớp.");

                string museDir = registry.GetUserDataDir("museai_channel1", BrowserProfileKind.MuseAI);
                Assert(Path.GetFullPath(museDir) == Path.Combine(tempDir, "museai_channel1"), "Đường dẫn MuseAI không khớp.");

                // Chặn Profile ID chứa email
                bool emailBlocked = false;
                try
                {
                    registry.GetUserDataDir("user@gmail.com", BrowserProfileKind.MuseAI);
                }
                catch (SecurityException)
                {
                    emailBlocked = true;
                }
                Assert(emailBlocked, "Profile ID chứa email phải bị chặn bằng SecurityException.");

                // Chặn Directory Traversal
                bool traversalBlocked = false;
                try
                {
                    registry.GetUserDataDir("../../../Windows/System32", BrowserProfileKind.SharedAI);
                }
                catch (SecurityException)
                {
                    traversalBlocked = true;
                }
                Assert(traversalBlocked, "Profile ID chứa ký tự traversal phải bị chặn bằng SecurityException.");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 2: Đảm bảo Single-Instance Profile Lock (In-process và File Lock).
    /// </summary>
    public static async Task Test_SingleInstanceProfileLock_InProcessAndCrossProcess()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "titoai_lock_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var owner = new ProfileOwner(profileId => Path.Combine(tempDir, profileId));
            string profileId = "shared_ai";

            // Owner 1 chiếm lock
            await using var lock1 = await owner.AcquireOwnerLockAsync(profileId, "JobWorker-1", TimeSpan.FromSeconds(2));
            Assert(owner.IsLocked(profileId), "Profile phải ở trạng thái đã bị khóa.");
            Assert(owner.GetCurrentOwnerTag(profileId) == "JobWorker-1", "Tag owner phải là JobWorker-1.");

            // Owner 2 cố gắng chiếm lock của cùng profileId -> phải ném TimeoutException
            bool lockBlocked = false;
            try
            {
                await owner.AcquireOwnerLockAsync(profileId, "JobWorker-2", TimeSpan.FromMilliseconds(100));
            }
            catch (TimeoutException)
            {
                lockBlocked = true;
            }
            Assert(lockBlocked, "Owner 2 chiếm cùng profile phải bị TimeoutException.");

            // Kiểm tra file lock cấp hệ điều hành không cho phép process/stream khác mở
            string lockFilePath = Path.Combine(tempDir, profileId, ".titoai_lock");
            Assert(File.Exists(lockFilePath), "Tệp .titoai_lock phải tồn tại trên đĩa.");

            bool fileLocked = false;
            try
            {
                using var stream = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                fileLocked = true;
            }
            Assert(fileLocked, "File .titoai_lock phải bị khóa cấp HĐH (IOException).");

            // Giải phóng lock 1
            await lock1.DisposeAsync();
            Assert(!owner.IsLocked(profileId), "Profile phải mở khóa sau khi Dispose.");

            // Sau khi giải phóng, Owner 2 có thể chiếm thành công
            await using var lock2 = await owner.AcquireOwnerLockAsync(profileId, "JobWorker-2", TimeSpan.FromSeconds(1));
            Assert(owner.IsLocked(profileId), "Owner 2 phải chiếm được khóa sau khi Owner 1 giải phóng.");
            Assert(owner.GetCurrentOwnerTag(profileId) == "JobWorker-2", "Tag owner phải là JobWorker-2.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// Test 3: Tab Pool quản lý 3 role tabs cho Shared AI, cách ly ngữ cảnh, tối đa 1 job/tab.
    /// </summary>
    public static async Task Test_TabPool_SharedAi_QuotaAndRoleIsolation()
    {
        var pool = new TabPool();
        string profileId = "shared_ai";

        // Chặn role không hợp lệ cho Shared AI
        bool invalidRoleBlocked = false;
        try
        {
            await pool.AcquireTabAsync(profileId, "CustomInvalidRole", "job-999");
        }
        catch (ArgumentException)
        {
            invalidRoleBlocked = true;
        }
        Assert(invalidRoleBlocked, "Role không nằm trong danh sách chuẩn của Shared AI phải bị từ chối.");

        // Cấp phát tab Script cho job-1
        await using var tabScript = await pool.AcquireTabAsync(profileId, SharedAiTabRole.Script, "job-1");
        Assert(pool.IsRoleActive(profileId, SharedAiTabRole.Script), "Tab Script phải đang hoạt động.");
        Assert(pool.GetActiveJobCount(profileId) == 1, "ActiveJobCount phải là 1.");

        // Cố tình cấp phát thêm job-2 vào tab Script -> phải ném InvalidOperationException (1 job/tab)
        bool concurrentTabBlocked = false;
        try
        {
            await pool.AcquireTabAsync(profileId, SharedAiTabRole.Script, "job-2");
        }
        catch (InvalidOperationException)
        {
            concurrentTabBlocked = true;
        }
        Assert(concurrentTabBlocked, "Không được phép cấp phát 2 job đồng thời trên cùng một tab.");

        // Cấp phát tiếp tab Image và VideoPrompt
        await using var tabImage = await pool.AcquireTabAsync(profileId, SharedAiTabRole.Image, "job-2");
        await using var tabVideo = await pool.AcquireTabAsync(profileId, SharedAiTabRole.VideoPrompt, "job-3");

        Assert(pool.GetActiveJobCount(profileId) == 3, "ActiveJobCount phải là 3 khi cả 3 role tab đều có job.");
        var activeTabs = pool.GetActiveTabs(profileId);
        Assert(activeTabs.Count == 3, "Danh sách active tabs phải có 3 phần tử.");

        // Giải phóng tab Script
        await tabScript.DisposeAsync();
        Assert(!pool.IsRoleActive(profileId, SharedAiTabRole.Script), "Tab Script phải được giải phóng.");
        Assert(pool.GetActiveJobCount(profileId) == 2, "ActiveJobCount phải giảm về 2.");

        // Cấp phát lại tab Script cho job mới sau khi đã giải phóng
        await using var tabScriptNew = await pool.AcquireTabAsync(profileId, SharedAiTabRole.Script, "job-4");
        Assert(pool.IsRoleActive(profileId, SharedAiTabRole.Script), "Tab Script phải được cấp phát lại thành công.");
    }

    /// <summary>
    /// Test 4: Mở đăng nhập đồng thời nhiều profile độc lập, Single-Instance Lock theo profile và kiểm tra ngân sách tài nguyên.
    /// </summary>
    public static async Task Test_LoginCoordinator_ConcurrentLogins_IndependentProfiles_AndResourceBudget()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            var registry = new ProfileRegistry(factory);
            await registry.EnsureProfileAsync("shared_ai", BrowserProfileKind.SharedAI);
            await registry.EnsureProfileAsync("museai_channel1", BrowserProfileKind.MuseAI);
            await registry.EnsureProfileAsync("museai_channel2", BrowserProfileKind.MuseAI);

            using var coordinator = new LoginCoordinator(registry);

            // 1. Profile 1 yêu cầu login -> nhận VisibleActive
            var res1 = await coordinator.RequestLoginAsync("shared_ai");
            Assert(!res1.Queued, "Profile 1 không bị xếp hàng.");
            Assert(res1.VisibilityState == BrowserVisibilityState.VisibleActive, "Profile 1 phải nhận VisibleActive.");
            Assert(res1.CurrentHolder == "shared_ai", "CurrentHolder phải là shared_ai.");
            Assert(coordinator.IsVisibleLoginActive, "IsVisibleLoginActive phải là true.");
            Assert(coordinator.IsProfileVisible("shared_ai"), "shared_ai phải đang visible.");

            // 2. Profile 2 yêu cầu login đồng thời -> nhận ngay VisibleActive, không bị xếp hàng chờ profile 1
            var res2 = await coordinator.RequestLoginAsync("museai_channel1");
            Assert(!res2.Queued, "Profile 2 không bị xếp hàng.");
            Assert(res2.VisibilityState == BrowserVisibilityState.VisibleActive, "Profile 2 phải nhận VisibleActive đồng thời.");
            Assert(res2.CurrentHolder == "museai_channel1", "CurrentHolder phải là museai_channel1.");
            Assert(coordinator.IsProfileVisible("museai_channel1"), "museai_channel1 phải đang visible.");
            Assert(coordinator.ActiveVisibleProfileIds.Count == 2, "Cả hai profile phải cùng hiển thị.");

            // 3. Bấm liên tiếp cùng một profile (shared_ai): không mở trùng
            var res1Repeat = await coordinator.RequestLoginAsync("shared_ai");
            Assert(!res1Repeat.Queued, "Lần bấm lặp lại không bị queued.");
            Assert(res1Repeat.VisibilityState == BrowserVisibilityState.VisibleActive, "Vẫn là VisibleActive.");
            Assert(coordinator.ActiveVisibleProfileIds.Count == 2, "Số lượng cửa sổ vẫn giữ nguyên là 2.");

            // 4. Đóng cửa sổ Profile 1 -> Profile 2 vẫn hoạt động bình thường
            await coordinator.CloseLoginWindowAsync("shared_ai", BrowserReasonCode.AuthClosed);
            Assert(!coordinator.IsProfileVisible("shared_ai"), "shared_ai phải không còn visible.");
            Assert(coordinator.IsProfileVisible("museai_channel1"), "museai_channel1 vẫn phải đang visible!");
            Assert(coordinator.IsVisibleLoginActive, "Vẫn còn cửa sổ hiển thị (museai_channel1).");

            var p1 = await registry.GetProfileAsync("shared_ai");
            Assert(p1?.VisibilityState == BrowserVisibilityState.Hidden, "shared_ai phải chuyển về Hidden.");
            var p2 = await registry.GetProfileAsync("museai_channel1");
            Assert(p2?.VisibilityState == BrowserVisibilityState.VisibleActive, "museai_channel1 phải giữ nguyên VisibleActive.");

            // 5. Đóng tiếp cửa sổ Profile 2
            await coordinator.CloseLoginWindowAsync("museai_channel1", BrowserReasonCode.AuthClosed);
            Assert(!coordinator.IsVisibleLoginActive, "Toàn bộ cửa sổ hiển thị đã được đóng.");

            // 6. Mở lại sau khi đóng: hoạt động bình thường, không kẹt khóa
            var resReopen = await coordinator.RequestLoginAsync("shared_ai");
            Assert(resReopen.VisibilityState == BrowserVisibilityState.VisibleActive, "Mở lại shared_ai thành công.");
            await coordinator.CloseLoginWindowAsync("shared_ai", BrowserReasonCode.AuthClosed);

            // 7. Kiểm tra ngân sách tài nguyên max_open_browsers
            int budgetLimit = 2;
            using var budgetCoordinator = new LoginCoordinator(
                registry,
                getMaxOpenBrowsers: () => budgetLimit);

            var bRes1 = await budgetCoordinator.RequestLoginAsync("shared_ai");
            Assert(bRes1.VisibilityState == BrowserVisibilityState.VisibleActive, "Profile 1 mở thành công trong budget.");

            var bRes2 = await budgetCoordinator.RequestLoginAsync("museai_channel1");
            Assert(bRes2.VisibilityState == BrowserVisibilityState.VisibleActive, "Profile 2 mở thành công trong budget.");

            // Thử mở profile thứ 3 vượt budget (2) -> Phải ném ngoại lệ rõ ràng về tài nguyên
            bool budgetExhaustedCaught = false;
            try
            {
                await budgetCoordinator.RequestLoginAsync("museai_channel2");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ngân sách tài nguyên"))
            {
                budgetExhaustedCaught = true;
            }

            Assert(budgetExhaustedCaught, "Phải ném InvalidOperationException khi đạt giới hạn tài nguyên max_open_browsers.");
        }
    }

    /// <summary>
    /// Test 5: Người dùng đóng cửa sổ đăng nhập -> phát auth_closed và tuyệt đối không tự mở lại.
    /// </summary>
    public static async Task Test_LoginCoordinator_UserClosesWindow_AuthClosedReasonCode_NoAutoReopen()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            var registry = new ProfileRegistry(factory);
            await registry.EnsureProfileAsync("shared_ai", BrowserProfileKind.SharedAI);

            BrowserProfileStateChangedPayload? capturedPayload = null;
            registry.ProfileStateChanged += payload =>
            {
                capturedPayload = payload;
                return Task.CompletedTask;
            };

            using var coordinator = new LoginCoordinator(registry);

            await coordinator.RequestLoginAsync("shared_ai");
            Assert(coordinator.CurrentVisibleProfileId == "shared_ai", "shared_ai phải đang giữ cửa sổ hiển thị.");

            // Người dùng đóng cửa sổ
            await coordinator.CloseLoginWindowAsync("shared_ai", BrowserReasonCode.AuthClosed);

            Assert(!coordinator.IsVisibleLoginActive, "Cửa sổ phải được đóng.");
            Assert(coordinator.CurrentVisibleProfileId == null, "Không còn profile nào giữ slot hiển thị.");

            var p = await registry.GetProfileAsync("shared_ai");
            Assert(p?.VisibilityState == BrowserVisibilityState.Hidden, "Profile phải chuyển về Hidden.");
            Assert(p?.OperationalState == BrowserOperationalState.Stopped, "Profile phải chuyển về Stopped.");

            Assert(capturedPayload != null, "Phải phát sự kiện ProfileStateChanged.");
            Assert(capturedPayload?.ReasonCode == BrowserReasonCode.AuthClosed, "ReasonCode phải là auth_closed.");
        }
    }

    /// <summary>
    /// Test 6: Quy trình Quiescing và Drain an toàn cho Shared AI (dừng tab chưa submit, bảo toàn in-flight).
    /// </summary>
    public static async Task Test_QuiescingAndDrain_SharedAi_SafeRollbackPendingTabs()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            var registry = new ProfileRegistry(factory);
            await registry.EnsureProfileAsync("shared_ai", BrowserProfileKind.SharedAI);

            var pool = new TabPool();
            using var loginCoord = new LoginCoordinator(registry);
            var handoff = new BrowserHandoffCoordinator(registry, pool, loginCoord, factory);

            string profileId = "shared_ai";

            // Tab 1: Chưa submit side-effect (đang đọc/chuẩn bị)
            var tab1 = await pool.AcquireTabAsync(profileId, SharedAiTabRole.Script, "job-read-1");
            tab1.IsSideEffectSubmitted = false;

            // Tab 2: Đã submit side-effect (đang chờ kết quả từ AI website)
            var tab2 = await pool.AcquireTabAsync(profileId, SharedAiTabRole.Image, "job-submitted-2");
            tab2.IsSideEffectSubmitted = true;

            Assert(pool.GetActiveJobCount(profileId) == 2, "Ban đầu có 2 tab active.");

            // Bước 1: Pause Dispatch
            await handoff.PauseDispatchAsync(profileId);
            Assert(handoff.IsDispatchPaused(profileId), "Cờ PauseDispatch phải bật.");

            // Bước 2: Drain Pending Tabs (dừng an toàn tab chưa submit)
            var drainedJobs = new List<string>();
            await handoff.DrainPendingTabsAsync(profileId, jobId =>
            {
                drainedJobs.Add(jobId);
                return Task.CompletedTask;
            });

            Assert(drainedJobs.Contains("job-read-1"), "Job-read-1 phải được chuyển giao drain.");
            Assert(!pool.IsRoleActive(profileId, SharedAiTabRole.Script), "Tab Script chưa submit phải được release ngay.");
            Assert(pool.IsRoleActive(profileId, SharedAiTabRole.Image), "Tab Image đã submit side effect phải tiếp tục giữ.");
            Assert(pool.GetActiveJobCount(profileId) == 1, "Chỉ còn 1 tab đang in-flight.");

            // Giải phóng nốt tab đã submit sau khi có kết quả
            await tab2.DisposeAsync();
            Assert(pool.GetActiveJobCount(profileId) == 0, "Toàn bộ tab đã hoàn tất.");

            // Bước 4: Prepare Handoff to Visible Login
            await handoff.PrepareHandoffToVisibleLoginAsync(profileId, "job-submitted-2", BrowserReasonCode.SessionExpired);

            // Kiểm tra Checkpoint được lưu bền vững trong SQLite
            await using (var db = factory())
            {
                var checkpoint = await db.BrowserHandoffCheckpoints
                    .FirstOrDefaultAsync(c => c.ProfileId == profileId && c.ResolvedAtUtc == null);
                Assert(checkpoint != null, "Phải có checkpoint handoff chưa resolved trong SQLite.");
                Assert(checkpoint?.ReasonCode == BrowserReasonCode.SessionExpired, "ReasonCode checkpoint không khớp.");
            }

            // Giả lập trạng thái session đã kết nối hợp lệ sau khi người dùng đăng nhập thành công
            await registry.UpdateProfileStateAsync(profileId, sessionStatus: BrowserSessionStatus.Connected);

            // Hoàn tất handoff sau khi đăng nhập thành công và probe xác nhận Connected
            await handoff.CompleteLoginHandoffAsync(profileId, authSuccess: true);

            await using (var db = factory())
            {
                var checkpoint = await db.BrowserHandoffCheckpoints
                    .FirstOrDefaultAsync(c => c.ProfileId == profileId);
                Assert(checkpoint?.ResolvedAtUtc != null, "Checkpoint phải được đánh dấu resolved.");
            }

            var updatedProfile = await registry.GetProfileAsync(profileId);
            Assert(updatedProfile?.VisibilityState == BrowserVisibilityState.Hidden, "Profile phải về Hidden sau handoff.");
            Assert(updatedProfile?.OperationalState == BrowserOperationalState.Idle, "Profile phải về Idle.");
            Assert(updatedProfile?.SessionStatus == BrowserSessionStatus.Connected, "SessionStatus phải là Connected.");
        }
    }

    /// <summary>
    /// Test 7: Phục hồi sau sự cố tại Startup (Reconcile Checkpoints & Dirty Profiles).
    /// </summary>
    public static async Task Test_CrashRecovery_ReconcileCheckpointsAtStartup()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string now = DateTime.UtcNow.ToString("o");

            // Giả lập trạng thái SQLite trước khi crash: profile đang Running/VisibleActive, checkpoint chưa resolved
            await using (var db = factory())
            {
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "shared_ai",
                    Kind = BrowserProfileKind.SharedAI,
                    VisibilityState = BrowserVisibilityState.VisibleActive,
                    OperationalState = BrowserOperationalState.Running,
                    SessionStatus = BrowserSessionStatus.Connected,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.BrowserHandoffCheckpoints.Add(new BrowserHandoffCheckpoint
                {
                    Id = Guid.NewGuid().ToString(),
                    ProfileId = "shared_ai",
                    ActiveJobId = "job-crashed",
                    PreviousState = BrowserOperationalState.Running,
                    TargetVisibilityState = BrowserVisibilityState.VisibleActive,
                    ReasonCode = BrowserReasonCode.SessionExpired,
                    CreatedAtUtc = now,
                    ResolvedAtUtc = null
                });

                await db.SaveChangesAsync();
            }

            var registry = new ProfileRegistry(factory);

            // Ứng dụng khởi động lại -> gọi ReconcileCheckpointsAtStartupAsync
            await registry.ReconcileCheckpointsAtStartupAsync();

            await using (var db = factory())
            {
                var checkpoint = await db.BrowserHandoffCheckpoints.FirstAsync();
                Assert(checkpoint.ResolvedAtUtc != null, "Checkpoint treo phải được giải quyết tại startup.");

                var profile = await db.BrowserProfiles.FirstAsync();
                Assert(profile.VisibilityState == BrowserVisibilityState.Hidden, "VisibilityState phải được reset về Hidden.");
                Assert(profile.OperationalState == BrowserOperationalState.Stopped, "OperationalState phải được reset về Stopped.");
                // Không phục hồi Connected từ metadata cũ nếu tiến trình đã chết!
                Assert(profile.SessionStatus == BrowserSessionStatus.Unverified, "SessionStatus phải chuyển về Unverified.");
            }
        }
    }

    /// <summary>
    /// Test 8: Session Probe & IProfileIdentityResolver - không bao giờ fake PASS khi chưa xác thực.
    /// </summary>
    public static async Task Test_SessionProbe_And_IProfileIdentityResolver_NoFakePass()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            var registry = new ProfileRegistry(factory);
            await registry.EnsureProfileAsync("museai_unlinked", BrowserProfileKind.MuseAI);

            IProfileIdentityResolver resolver = new SessionProbe(registry);
            ISessionProbe probe = (ISessionProbe)resolver;

            // 1. Khi chưa có adapter thật và session chưa thẩm định -> Bắt buộc IsSuccess = false, SessionStatus = Unverified
            var unverifiedResult = await resolver.ResolveProfileIdentityAsync("museai_unlinked", "MuseAI");
            Assert(!unverifiedResult.IsSuccess, "ResolveProfileIdentityAsync tuyệt đối không được fake PASS.");
            Assert(unverifiedResult.SessionStatus == BrowserSessionStatus.Unverified, "SessionStatus phải là Unverified.");
            Assert(!string.IsNullOrWhiteSpace(unverifiedResult.ErrorMessage), "Phải có thông báo lỗi giải thích rõ ràng.");

            var probeResult = await probe.CheckSessionAsync("museai_unlinked");
            Assert(probeResult.SessionStatus == BrowserSessionStatus.Unverified, "Probe sessionStatus phải là Unverified.");

            // 2. Với profile đã Connected và có AccountIdentity hợp lệ
            await registry.EnsureProfileAsync("museai_linked", BrowserProfileKind.MuseAI, accountIdentity: "channel_tiktok_01");
            await registry.UpdateProfileStateAsync("museai_linked", sessionStatus: BrowserSessionStatus.Connected);

            var verifiedResult = await resolver.ResolveProfileIdentityAsync("museai_linked", "MuseAI");
            Assert(verifiedResult.IsSuccess, "Profile đã Connected và có AccountIdentity phải thành công.");
            Assert(verifiedResult.AccountIdentity == "channel_tiktok_01", "AccountIdentity phải khớp.");
            Assert(verifiedResult.SessionStatus == BrowserSessionStatus.Connected, "SessionStatus phải là Connected.");

            // 3. Với profile không tồn tại trong hệ thống
            var notFoundResult = await resolver.ResolveProfileIdentityAsync("non_existent_profile", "MuseAI");
            Assert(!notFoundResult.IsSuccess, "Profile không tồn tại phải trả về false.");
        }
    }

    /// <summary>
    /// Test 9: CompleteLoginHandoff - authSuccess = true nhưng SessionProbe trả về Unverified ->
    /// Checkpoint tuyệt đối KHÔNG được resolve, profile về Stopped, Unverified (chống ảo tưởng xác thực).
    /// </summary>
    public static async Task Test_CompleteLoginHandoff_ProbeUnverified_CheckpointNotResolved()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            var registry = new ProfileRegistry(factory);
            string profileId = "shared_ai";
            await registry.EnsureProfileAsync(profileId, BrowserProfileKind.SharedAI);

            var pool = new TabPool();
            using var loginCoord = new LoginCoordinator(registry);
            var handoff = new BrowserHandoffCoordinator(registry, pool, loginCoord, factory);

            // Bắt đầu handoff hiển thị
            await handoff.PrepareHandoffToVisibleLoginAsync(profileId, "job-unverified-test", BrowserReasonCode.LoginRequired);

            // Xác nhận có checkpoint chưa resolved
            await using (var db = factory())
            {
                var cp = await db.BrowserHandoffCheckpoints.FirstOrDefaultAsync(c => c.ProfileId == profileId && c.ResolvedAtUtc == null);
                Assert(cp != null, "Checkpoint handoff phải tồn tại trong SQLite trước khi complete.");
            }

            // Gọi CompleteLoginHandoffAsync với authSuccess = true, nhưng session probe thực tế trả về Unverified
            await handoff.CompleteLoginHandoffAsync(profileId, authSuccess: true);

            // Kiểm tra: Checkpoint tuyệt đối KHÔNG được resolve
            await using (var db = factory())
            {
                var cp = await db.BrowserHandoffCheckpoints.FirstOrDefaultAsync(c => c.ProfileId == profileId);
                Assert(cp != null, "Checkpoint phải tồn tại.");
                Assert(cp?.ResolvedAtUtc == null, "Checkpoint tuyệt đối KHÔNG được đánh dấu resolved khi session unverified.");
                Assert(cp?.ReasonCode == "login_unverified", "ReasonCode của checkpoint phải được cập nhật là login_unverified.");
            }

            var updatedProfile = await registry.GetProfileAsync(profileId);
            Assert(updatedProfile?.VisibilityState == BrowserVisibilityState.Hidden, "Profile phải về Hidden.");
            Assert(updatedProfile?.OperationalState == BrowserOperationalState.Stopped, "Profile phải về Stopped (không được về Idle).");
            Assert(updatedProfile?.SessionStatus == BrowserSessionStatus.Unverified, "SessionStatus phải giữ Unverified.");

            // Xác nhận Semaphore hiển thị đã được giải phóng
            Assert(!loginCoord.IsVisibleLoginActive, "Slot hiển thị của loginCoordinator phải được giải phóng.");

            // Xác nhận cờ PauseDispatch đã được gỡ bỏ
            Assert(!handoff.IsDispatchPaused(profileId), "Cờ PauseDispatch phải được bỏ.");
        }
    }

    /// <summary>
    /// Test 11: PlaywrightBrowserController - Xử lý lỗi driver Playwright chưa cài đặt (PlaywrightException),
    /// đảm bảo ném lỗi có cấu trúc, cập nhật Error state và GIẢI PHÓNG lock .titoai_lock an toàn (không rò rỉ).
    /// </summary>
    public static async Task Test_PlaywrightBrowserController_MissingDriverException_ReleasesLockSafely()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_pw_driver_err_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                string profileId = "shared_ai";
                await registry.EnsureProfileAsync(profileId, BrowserProfileKind.SharedAI);

                using var owner = new ProfileOwner(pid => registry.GetUserDataDir(pid, BrowserProfileKind.SharedAI));

                BrowserProfileStateChangedPayload? capturedPayload = null;
                registry.ProfileStateChanged += payload =>
                {
                    capturedPayload = payload;
                    return Task.CompletedTask;
                };

                // Launcher ném PlaywrightException giả lập máy chưa cài đặt Playwright Chromium driver
                var controller = new PlaywrightBrowserController(
                    registry,
                    owner,
                    customPersistentLauncher: (userDataDir, options) =>
                    {
                        throw new PlaywrightException("Executable doesn't exist at C:\\Users\\PC\\AppData\\Local\\ms-playwright\\chromium-1155\\chrome-win\\chrome.exe");
                    });

                await using (controller)
                {
                    bool exceptionCaught = false;
                    try
                    {
                        await controller.LaunchPersistentContextAsync(profileId, isHeadless: true);
                    }
                    catch (InvalidOperationException ex)
                    {
                        exceptionCaught = true;
                        Assert(ex.InnerException is PlaywrightException, "InnerException phải là PlaywrightException.");
                    }
                    Assert(exceptionCaught, "Phải ném InvalidOperationException có cấu trúc khi thiếu Playwright driver.");

                    // QUAN TRỌNG: Lock .titoai_lock phải được giải phóng an toàn, không bị treo/rò rỉ
                    Assert(!owner.IsLocked(profileId), "Lock single-instance phải được giải phóng sau lỗi PlaywrightException.");
                    Assert(!controller.IsRunning(profileId), "Controller không được ghi nhận tiến trình đang chạy.");

                    var profile = await registry.GetProfileAsync(profileId);
                    Assert(profile?.OperationalState == BrowserOperationalState.Error, "Profile phải chuyển về trạng thái Error.");
                    Assert(capturedPayload?.ReasonCode == "playwright_driver_error", "ReasonCode phát ra phải là playwright_driver_error.");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 13: Đóng trong lúc trang đang tải (GotoAsync) & đóng trước khi đăng nhập xong.
    /// Ngoại lệ phải được bắt an toàn, lock và semaphore được giải phóng 100%, có thể mở lại bình thường ngay sau đó.
    /// </summary>
    public static async Task Test_LoginLifecycle_CloseDuringGoto_ReleasesLockAndRecovers()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_pw_gotoclose_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                string profileId = "shared_ai";
                await registry.EnsureProfileAsync(profileId, BrowserProfileKind.SharedAI);

                using var owner = new ProfileOwner(pid => registry.GetUserDataDir(pid, BrowserProfileKind.SharedAI));

                bool simulateCrashOnGoto = true;
                MockBrowserContextProxy? currentMockContext = null;

                var controller = new PlaywrightBrowserController(
                    registry,
                    owner,
                    customPersistentLauncher: (userDataDir, options) =>
                    {
                        var mockContextProxy = DispatchProxy.Create<IBrowserContext, MockBrowserContextProxy>();
                        var mockContext = (MockBrowserContextProxy)(object)mockContextProxy;
                        currentMockContext = mockContext;

                        var mockPageProxy = DispatchProxy.Create<IPage, MockPageProxy>();
                        var mockPage = (MockPageProxy)(object)mockPageProxy;

                        if (simulateCrashOnGoto)
                        {
                            mockPage.CustomGoto = async url =>
                            {
                                await Task.Yield();
                                mockContext.SimulateClose();
                                throw new PlaywrightException("Target page, context or browser has been closed");
                            };
                        }

                        mockContext.AddPage(mockPageProxy);
                        return Task.FromResult(mockContextProxy);
                    });

                await using (controller)
                {
                    using var coordinator = new LoginCoordinator(registry, controller);

                    // 1. Mở login nhưng bị đóng đột ngột trong lúc tải trang
                    bool caughtExpectedException = false;
                    try
                    {
                        await coordinator.RequestLoginAsync(profileId, "https://chatgpt.com/login");
                    }
                    catch (Exception ex)
                    {
                        caughtExpectedException = ex is OperationCanceledException || ex is PlaywrightException;
                    }
                    Assert(caughtExpectedException, "Phải bắt được ngoại lệ an toàn khi đóng trong lúc tải trang.");

                    // Chờ dọn dẹp
                    for (int poll = 0; poll < 100 && (controller.IsRunning(profileId) || coordinator.IsVisibleLoginActive || owner.IsLocked(profileId)); poll++)
                    {
                        await Task.Delay(10);
                    }

                    // 2. Kiểm chứng: Lock và slot semaphore phải được giải phóng 100%
                    Assert(!controller.IsRunning(profileId), "Controller không được giữ trạng thái Running.");
                    Assert(!owner.IsLocked(profileId), "Lock ProfileOwner phải được giải phóng sau lỗi Goto.");
                    Assert(!coordinator.IsVisibleLoginActive, "Slot hiển thị của coordinator phải được giải phóng.");
                    Assert(coordinator.CurrentVisibleProfileId == null, "CurrentVisibleProfileId phải là null.");

                    string lockFilePath = Path.Combine(registry.GetUserDataDir(profileId, BrowserProfileKind.SharedAI), ".titoai_lock");
                    if (File.Exists(lockFilePath))
                    {
                        using var stream = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }

                    // 3. Mở lại ngay từ chính nút đăng nhập (lần này Goto thành công bình thường)
                    simulateCrashOnGoto = false;
                    var reopenResult = await coordinator.RequestLoginAsync(profileId, "https://chatgpt.com/login");
                    Assert(reopenResult.VisibilityState == BrowserVisibilityState.VisibleActive, "Mở lại phải thành công nhận VisibleActive.");
                    Assert(controller.IsRunning(profileId), "Controller phải báo IsRunning = true sau khi mở lại.");
                    Assert(owner.IsLocked(profileId), "Lock phải được chiếm cho phiên mới.");

                    // Dọn dẹp phiên mới
                    currentMockContext?.SimulateClose();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 14: Double-click mở login và mở lại ngay sau khi đóng.
    /// Không bị xung đột đồng thời, mở lại trơn tru không cần delay.
    /// </summary>
    public static async Task Test_LoginLifecycle_DoubleClickAndImmediateReopen()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_pw_doubleclick_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                string profileId = "shared_ai";
                await registry.EnsureProfileAsync(profileId, BrowserProfileKind.SharedAI);

                using var owner = new ProfileOwner(pid => registry.GetUserDataDir(pid, BrowserProfileKind.SharedAI));

                MockBrowserContextProxy? currentMockContext = null;

                var controller = new PlaywrightBrowserController(
                    registry,
                    owner,
                    customPersistentLauncher: (userDataDir, options) =>
                    {
                        var mockContextProxy = DispatchProxy.Create<IBrowserContext, MockBrowserContextProxy>();
                        var mockContext = (MockBrowserContextProxy)(object)mockContextProxy;
                        currentMockContext = mockContext;
                        return Task.FromResult(mockContextProxy);
                    });

                await using (controller)
                {
                    using var coordinator = new LoginCoordinator(registry, controller);

                    // 1. Double-click: 2 request đồng thời từ UI
                    var task1 = coordinator.RequestLoginAsync(profileId);
                    var task2 = coordinator.RequestLoginAsync(profileId);
                    var results = await Task.WhenAll(task1, task2);

                    Assert(results[0].VisibilityState == BrowserVisibilityState.VisibleActive, "Request 1 phải nhận VisibleActive.");
                    Assert(results[1].VisibilityState == BrowserVisibilityState.VisibleActive, "Request 2 (double click) phải nhận VisibleActive.");
                    Assert(coordinator.CurrentVisibleProfileId == profileId, "CurrentVisibleProfileId phải là profileId.");

                    // 2. Người dùng đóng nút X
                    currentMockContext!.SimulateClose();

                    for (int poll = 0; poll < 100 && (controller.IsRunning(profileId) || coordinator.IsVisibleLoginActive); poll++)
                    {
                        await Task.Delay(10);
                    }

                    // 3. Mở lại NGAY LẬP TỨC từ chính nút đăng nhập
                    var reopenResult = await coordinator.RequestLoginAsync(profileId);
                    Assert(reopenResult.VisibilityState == BrowserVisibilityState.VisibleActive, "Mở lại ngay lập tức phải thành công.");
                    Assert(coordinator.CurrentVisibleProfileId == profileId, "CurrentVisibleProfileId phải là profileId.");
                    Assert(controller.IsRunning(profileId), "Controller phải đang chạy phiên mới.");

                    // Dọn dẹp
                    currentMockContext?.SimulateClose();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 15: Event đóng phiên cũ đến sau khi phiên mới đã được mở.
    /// Sự kiện muộn của phiên 1 tuyệt đối KHÔNG làm mất trạng thái hay đóng nhầm phiên 2.
    /// </summary>
    public static async Task Test_LoginLifecycle_StaleCloseEventFromOldSession_DoesNotCorruptNewSession()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_pw_staleclose_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                string profileId = "shared_ai";
                await registry.EnsureProfileAsync(profileId, BrowserProfileKind.SharedAI);

                using var owner = new ProfileOwner(pid => registry.GetUserDataDir(pid, BrowserProfileKind.SharedAI));

                MockBrowserContextProxy? mockSession1 = null;
                MockBrowserContextProxy? mockSession2 = null;
                int launchCount = 0;

                var controller = new PlaywrightBrowserController(
                    registry,
                    owner,
                    customPersistentLauncher: (userDataDir, options) =>
                    {
                        launchCount++;
                        var mockContextProxy = DispatchProxy.Create<IBrowserContext, MockBrowserContextProxy>();
                        var mockContext = (MockBrowserContextProxy)(object)mockContextProxy;

                        if (launchCount == 1) mockSession1 = mockContext;
                        else mockSession2 = mockContext;

                        return Task.FromResult(mockContextProxy);
                    });

                await using (controller)
                {
                    using var coordinator = new LoginCoordinator(registry, controller);

                    // 1. Mở phiên 1
                    await coordinator.RequestLoginAsync(profileId);
                    Assert(launchCount == 1, "Phiên 1 đã khởi chạy.");
                    Assert(controller.IsRunning(profileId), "Controller đang chạy phiên 1.");

                    // 2. Giả lập đóng phiên 1 qua controller (chưa kích hoạt event SimulateClose của mockSession1)
                    await controller.CloseContextAsync(profileId);
                    Assert(!controller.IsRunning(profileId), "Controller đã dừng phiên 1.");

                    // 3. Mở phiên 2 ngay sau đó
                    coordinator.ReleaseVisibleSlot(profileId);
                    await coordinator.RequestLoginAsync(profileId);
                    Assert(launchCount == 2, "Phiên 2 đã khởi chạy.");
                    Assert(controller.IsRunning(profileId), "Controller đang chạy phiên 2.");
                    Assert(coordinator.CurrentVisibleProfileId == profileId, "CurrentVisibleProfileId là profileId.");

                    // 4. Kích hoạt sự kiện Close muộn từ phiên 1
                    Assert(mockSession1 != null, "MockSession1 phải tồn tại.");
                    mockSession1!.SimulateClose();

                    // Chờ một chút để event handler chạy xong
                    await Task.Delay(50);

                    // 5. Kiểm chứng: Phiên 2 VẪN ĐANG HOẠT ĐỘNG HOÀN TOÀN BÌNH THƯỜNG!
                    Assert(controller.IsRunning(profileId), "Controller VẪN PHẢI BÁO IsRunning = true cho phiên 2.");
                    Assert(owner.IsLocked(profileId), "Lock của phiên 2 không được bị giải phóng.");
                    Assert(coordinator.CurrentVisibleProfileId == profileId, "CurrentVisibleProfileId vẫn phải là profileId.");

                    var p = await registry.GetProfileAsync(profileId);
                    Assert(p?.OperationalState == BrowserOperationalState.Running, "Profile trong DB vẫn phải là Running.");
                    Assert(p?.VisibilityState == BrowserVisibilityState.VisibleActive, "Profile trong DB vẫn phải là VisibleActive.");

                    // 6. Đóng phiên 2 thật sự
                    Assert(mockSession2 != null, "MockSession2 phải tồn tại.");
                    mockSession2!.SimulateClose();

                    for (int poll = 0; poll < 100 && (controller.IsRunning(profileId) || coordinator.IsVisibleLoginActive); poll++)
                    {
                        await Task.Delay(10);
                    }

                    // Chờ ProfileRegistry hoàn tất cập nhật DB về Stopped
                    for (int poll = 0; poll < 100; poll++)
                    {
                        var closedProfile = await registry.GetProfileAsync(profileId);
                        if (closedProfile?.OperationalState == BrowserOperationalState.Stopped)
                        {
                            break;
                        }
                        await Task.Delay(10);
                    }

                    Assert(!controller.IsRunning(profileId), "Controller dừng hoàn toàn sau khi phiên 2 đóng.");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 16: Tách trạng thái browser mở khỏi trạng thái xác thực tài khoản (SessionStatus).
    /// Đóng cửa sổ không làm mất phiên đăng nhập (không suy ra đã logout hay unlinked).
    /// </summary>
    public static async Task Test_LoginLifecycle_SessionStatusPreserved_NoUnintendedLogout()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_pw_preserve_session_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                string profileId = "shared_ai";
                await registry.EnsureProfileAsync(profileId, BrowserProfileKind.SharedAI);

                // Giả lập tài khoản đã xác thực thành công trước đó
                await registry.UpdateProfileStateAsync(profileId, sessionStatus: BrowserSessionStatus.Connected);

                using var owner = new ProfileOwner(pid => registry.GetUserDataDir(pid, BrowserProfileKind.SharedAI));

                MockBrowserContextProxy? currentMockContext = null;

                var controller = new PlaywrightBrowserController(
                    registry,
                    owner,
                    customPersistentLauncher: (userDataDir, options) =>
                    {
                        var mockContextProxy = DispatchProxy.Create<IBrowserContext, MockBrowserContextProxy>();
                        var mockContext = (MockBrowserContextProxy)(object)mockContextProxy;
                        currentMockContext = mockContext;
                        return Task.FromResult(mockContextProxy);
                    });

                await using (controller)
                {
                    using var coordinator = new LoginCoordinator(registry, controller);

                    // Mở cửa sổ đăng nhập
                    await coordinator.RequestLoginAsync(profileId);

                    // Người dùng đóng cửa sổ bằng nút X
                    currentMockContext!.SimulateClose();

                    for (int poll = 0; poll < 100 && (controller.IsRunning(profileId) || coordinator.IsVisibleLoginActive); poll++)
                    {
                        await Task.Delay(10);
                    }

                    // Kiểm tra: Trạng thái hiển thị về Hidden/Stopped, NHƯNG SessionStatus VẪN PHẢI LÀ Connected!
                    var p = await registry.GetProfileAsync(profileId);
                    Assert(p?.VisibilityState == BrowserVisibilityState.Hidden, "VisibilityState phải là Hidden.");
                    Assert(p?.OperationalState == BrowserOperationalState.Stopped, "OperationalState phải là Stopped.");
                    Assert(p?.SessionStatus == BrowserSessionStatus.Connected, "SessionStatus tuyệt đối KHÔNG được mất hoặc chuyển thành LoggedOut!");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 17: Phòng thủ Stale State trong LoginCoordinator.RequestLoginAsync.
    /// Nếu _currentVisibleProfileId != null nhưng process controller không còn chạy (stale state),
    /// tự động dọn dẹp và cho phép mở lại bình thường thay vì kẹt.
    /// </summary>
    public static async Task Test_LoginCoordinator_StaleStateSelfHealing()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_pw_stale_defense_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                string profileId = "shared_ai";
                await registry.EnsureProfileAsync(profileId, BrowserProfileKind.SharedAI);

                using var owner = new ProfileOwner(pid => registry.GetUserDataDir(pid, BrowserProfileKind.SharedAI));

                MockBrowserContextProxy? currentMockContext = null;

                var controller = new PlaywrightBrowserController(
                    registry,
                    owner,
                    customPersistentLauncher: (userDataDir, options) =>
                    {
                        var mockContextProxy = DispatchProxy.Create<IBrowserContext, MockBrowserContextProxy>();
                        var mockContext = (MockBrowserContextProxy)(object)mockContextProxy;
                        currentMockContext = mockContext;
                        return Task.FromResult(mockContextProxy);
                    });

                await using (controller)
                {
                    using var coordinator = new LoginCoordinator(registry, controller);

                    // Tạo Stale State nhân tạo: Slot hiển thị bị giữ bởi shared_ai nhưng controller không chạy
                    bool slotAcquired = await coordinator.WaitForVisibleSlotAsync(profileId, TimeSpan.FromSeconds(1));
                    Assert(slotAcquired, "Slot hiển thị đã bị chiếm trước.");
                    Assert(coordinator.CurrentVisibleProfileId == profileId, "CurrentVisibleProfileId là shared_ai.");
                    Assert(!controller.IsRunning(profileId), "Controller thực tế KHÔNG chạy (Stale State).");

                    // Người dùng bấm nút Đăng nhập: Cơ chế self-healing phải tự dọn dẹp và mở persistent context mới
                    var result = await coordinator.RequestLoginAsync(profileId);
                    Assert(result.VisibilityState == BrowserVisibilityState.VisibleActive, "RequestLoginAsync phải thành công nhận VisibleActive.");
                    Assert(controller.IsRunning(profileId), "Controller phải được khởi chạy thành công sau khi self-heal.");
                    Assert(coordinator.CurrentVisibleProfileId == profileId, "CurrentVisibleProfileId là profileId.");

                    // Dọn dẹp
                    currentMockContext?.SimulateClose();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Mock Proxy tự động implement toàn bộ IBrowserContext thông qua BCL DispatchProxy.
    /// Hỗ trợ đăng ký và kích hoạt sự kiện Close (SimulateClose), quản lý mock Pages.
    /// </summary>
    public class MockBrowserContextProxy : DispatchProxy
    {
        public bool WasClosed { get; private set; }
        private readonly List<EventHandler<IBrowserContext>> _closeHandlers = new();
        private readonly List<IPage> _pages = new();
        private readonly object _lock = new();

        public void AddPage(IPage page)
        {
            lock (_lock)
            {
                _pages.Add(page);
            }
        }

        public void SimulateClose()
        {
            WasClosed = true;
            List<EventHandler<IBrowserContext>> handlers;
            lock (_lock)
            {
                handlers = [.. _closeHandlers];
            }
            foreach (var handler in handlers)
            {
                try
                {
                    handler.Invoke(this, (IBrowserContext)(object)this);
                }
                catch { }
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;

            if (targetMethod.Name == "add_Close" && args != null && args.Length > 0 && args[0] is EventHandler<IBrowserContext> addHandler)
            {
                lock (_lock)
                {
                    _closeHandlers.Add(addHandler);
                }
                return null;
            }

            if (targetMethod.Name == "remove_Close" && args != null && args.Length > 0 && args[0] is EventHandler<IBrowserContext> remHandler)
            {
                lock (_lock)
                {
                    _closeHandlers.Remove(remHandler);
                }
                return null;
            }

            if (targetMethod.Name == "get_Pages")
            {
                lock (_lock)
                {
                    return _pages.AsReadOnly();
                }
            }

            if (targetMethod.Name == "CloseAsync")
            {
                SimulateClose();
                return Task.CompletedTask;
            }

            if (targetMethod.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (targetMethod.ReturnType.IsGenericType &&
                targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
                var defaultVal = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                var fromResultMethod = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType);
                return fromResultMethod.Invoke(null, [defaultVal]);
            }

            if (targetMethod.ReturnType.IsValueType)
            {
                return Activator.CreateInstance(targetMethod.ReturnType);
            }

            return null;
        }
    }

    /// <summary>
    /// Mock Proxy cho IPage để kiểm thử điều hướng và đóng trong lúc tải.
    /// </summary>
    public class MockPageProxy : DispatchProxy
    {
        public bool IsClosed { get; set; }
        public string Url { get; set; } = "about:blank";
        public Func<string, Task>? CustomGoto { get; set; }
        private readonly List<EventHandler<IPage>> _closeHandlers = new();
        private readonly object _lock = new();

        public void SimulateClose()
        {
            IsClosed = true;
            List<EventHandler<IPage>> handlers;
            lock (_lock)
            {
                handlers = [.. _closeHandlers];
            }
            foreach (var handler in handlers)
            {
                try
                {
                    handler.Invoke(this, (IPage)(object)this);
                }
                catch { }
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;

            if (targetMethod.Name == "get_IsClosed") return IsClosed;
            if (targetMethod.Name == "get_Url") return Url;

            if (targetMethod.Name == "add_Close" && args != null && args.Length > 0 && args[0] is EventHandler<IPage> addHandler)
            {
                lock (_lock) { _closeHandlers.Add(addHandler); }
                return null;
            }

            if (targetMethod.Name == "remove_Close" && args != null && args.Length > 0 && args[0] is EventHandler<IPage> remHandler)
            {
                lock (_lock) { _closeHandlers.Remove(remHandler); }
                return null;
            }

            if (targetMethod.Name == "GotoAsync" && args != null && args.Length > 0 && args[0] is string url)
            {
                Url = url;
                if (CustomGoto != null)
                {
                    return CustomGoto(url);
                }
                return Task.FromResult<IResponse?>(null);
            }

            if (targetMethod.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (targetMethod.ReturnType.IsGenericType &&
                targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
                var defaultVal = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                var fromResultMethod = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType);
                return fromResultMethod.Invoke(null, [defaultVal]);
            }

            if (targetMethod.ReturnType.IsValueType)
            {
                return Activator.CreateInstance(targetMethod.ReturnType);
            }

            return null;
        }
    }

    /// <summary>
    /// Chạy toàn bộ các bài kiểm tra khói của T13.
    /// </summary>
    public static async Task RunAllAsync()
    {
        Console.WriteLine("\n[T13] Browser Runtime, Profile Registry & Login Handoff Smoke Checks:");

        async Task RunAsync(string name, Func<Task> test)
        {
            Console.Write($"  [RUN] {name} ... ");
            try
            {
                await test();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL");
                Console.ResetColor();
                Console.WriteLine($"        -> Lỗi: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        await RunAsync("Test 1: Profile Registry & Path Safety (No email, traversal blocked)", Test_ProfileRegistry_PathSafety_NoEmailOrTraversal);
        await RunAsync("Test 2: Single-Instance Profile Lock (In-process & Cross-process/FileLock)", Test_SingleInstanceProfileLock_InProcessAndCrossProcess);
        await RunAsync("Test 3: Tab Pool Shared AI 3 Roles, Isolation & Quota Limits", Test_TabPool_SharedAi_QuotaAndRoleIsolation);
        await RunAsync("Test 4: Concurrent Logins for Independent Profiles, Single-Instance Per-Profile & Resource Budget", Test_LoginCoordinator_ConcurrentLogins_IndependentProfiles_AndResourceBudget);
        await RunAsync("Test 5: User Closes Window -> auth_closed & No Auto-reopen", Test_LoginCoordinator_UserClosesWindow_AuthClosedReasonCode_NoAutoReopen);
        await RunAsync("Test 6: Quiescing & Drain Semantics for Shared AI", Test_QuiescingAndDrain_SharedAi_SafeRollbackPendingTabs);
        await RunAsync("Test 7: Crash Recovery at Startup (Reconcile Checkpoints & Dirty Profiles)", Test_CrashRecovery_ReconcileCheckpointsAtStartup);
        await RunAsync("Test 8: Session Probe & IProfileIdentityResolver Contracts (No fake PASS)", Test_SessionProbe_And_IProfileIdentityResolver_NoFakePass);
        await RunAsync("Test 9: CompleteLoginHandoff with Unverified SessionProbe does NOT resolve Checkpoint", Test_CompleteLoginHandoff_ProbeUnverified_CheckpointNotResolved);
        await RunAsync("Test 11: PlaywrightBrowserController Missing Driver Error Handling & Lock Release", Test_PlaywrightBrowserController_MissingDriverException_ReleasesLockSafely);
        await RunAsync("Test 13: Login Lifecycle: Close during GotoAsync (lock released & recovered)", Test_LoginLifecycle_CloseDuringGoto_ReleasesLockAndRecovers);
        await RunAsync("Test 14: Login Lifecycle: Double-click & Immediate Reopen", Test_LoginLifecycle_DoubleClickAndImmediateReopen);
        await RunAsync("Test 15: Login Lifecycle: Stale Close Event from Old Session ignored", Test_LoginLifecycle_StaleCloseEventFromOldSession_DoesNotCorruptNewSession);
        await RunAsync("Test 16: Login Lifecycle: SessionStatus preserved across window closes", Test_LoginLifecycle_SessionStatusPreserved_NoUnintendedLogout);
        await RunAsync("Test 17: Login Coordinator: Stale State Self-Healing", Test_LoginCoordinator_StaleStateSelfHealing);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
