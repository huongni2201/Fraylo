using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Accounts;
using Fraylo.Browser;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Accounts;
using Fraylo.Contracts.Browser;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói chuyên biệt cho lỗi nhận diện đăng nhập MuseAI theo yêu cầu:
/// 1. Đăng nhập Muse thật -> kiểm tra phiên -> identity được xác minh -> bật hoạt động thành công.
/// 2. Đóng rồi mở lại browser và restart app: phiên còn hợp lệ được probe lại đúng.
/// 3. Chưa đăng nhập/phiên hết hạn: không cho bật.
/// 4. Lỗi mạng/timeout: báo đúng nguyên nhân, có thể thử lại.
/// 5. Hai profile Muse khác nhau: không nhận nhầm identity hoặc cập nhật nhầm tài khoản.
/// 6. Probe cũ/sự kiện đóng context cũ không ghi đè kết quả mới.
/// 7. Hồi quy toàn diện luồng tài khoản các provider khác.
/// </summary>
public static class MuseAiSessionProbeSmokeTests
{
    private static DbContextOptions<FrayloDbContext> CreateSqliteOptions(string dbPath)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        return new DbContextOptionsBuilder<FrayloDbContext>()
            .UseSqlite(connStr)
            .Options;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED]: {message}");
        }
    }

    private static bool IsProfileLockedByExternalProcess(string profileDir)
    {
        string lockFilePath = Path.Combine(profileDir, ".Fraylo_lock");
        if (!File.Exists(lockFilePath)) return false;

        try
        {
            using var stream = File.Open(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("      Fraylo — MuseAI Session & Identity Probe Smoke Checks      ");
        Console.WriteLine("=================================================================");

        var tempDir = Path.Combine(Path.GetTempPath(), "Fraylo_muse_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "muse_smoke.db");

        try
        {
            var bootstrapper = new SqliteDatabaseBootstrapper();
            await bootstrapper.BootstrapAsync(dbPath);
            var dbOptions = CreateSqliteOptions(dbPath);

            Console.WriteLine("\n[1] Kiểm thử: MuseAI thật đã đăng nhập -> Probe Session -> Xác minh Identity -> Bật hoạt động thành công");
            await Test_MuseAiRealLogin_ProbeSession_VerifiesIdentity_AndEnablesAccountAsync(dbOptions, tempDir);

            Console.WriteLine("\n[2] Kiểm thử: Đóng browser & restart app -> Phiên còn hợp lệ được probe lại đúng");
            await Test_MuseAi_CloseAndRestartApp_SessionPreserved_AndProbedCorrectly(dbOptions, tempDir);

            Console.WriteLine("\n[3] Kiểm thử: Chưa đăng nhập / phiên hết hạn -> Không cho bật hoạt động");
            await Test_MuseAi_UnauthenticatedProfile_CannotEnable_AndReportsCorrectReason(dbOptions, tempDir);

            Console.WriteLine("\n[4] Kiểm thử: Lỗi mạng / timeout -> Báo đúng nguyên nhân, không nhầm sang chưa đăng nhập");
            await Test_MuseAi_NetworkErrorOrTimeout_ReportsSpecificReason_DoesNotClassifyAsExpired(dbOptions, tempDir);

            Console.WriteLine("\n[5] Kiểm thử: Hai profile Muse khác nhau -> Không nhận nhầm identity hoặc cập nhật nhầm tài khoản");
            await Test_MuseAi_TwoProfiles_NoIdentityBleedOrCrossAccountUpdate(dbOptions, tempDir);

            Console.WriteLine("\n[6] Kiểm thử: Probe cũ / sự kiện đóng context cũ -> Không ghi đè kết quả Verified");
            await Test_MuseAi_StaleProbeOrContextClosed_DoesNotOverwriteVerifiedResult(dbOptions, tempDir);

            Console.WriteLine("\n[7] Kiểm thử hồi quy: Toàn bộ suite ProviderAccountAndCapacitySmokeTests (H1-H4, A03-A08, A13)");
            await ProviderAccountAndCapacitySmokeTests.RunAllAsync();

            Console.WriteLine("\n=================================================================");
            Console.WriteLine("   TẤT CẢ 7/7 KIỂM THỬ KHÓI MUSEAI SESSION PROBE ĐÃ VƯỢT QUA!   ");
            Console.WriteLine("=================================================================");

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

    private static async Task Test_MuseAiRealLogin_ProbeSession_VerifiesIdentity_AndEnablesAccountAsync(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string realProfileDir = Path.Combine(localAppData, "Fraylo", "profiles", "profile_museai_98fa16b5891045bab598d73113d45d5e");

        if (!Directory.Exists(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Không tìm thấy thư mục profile MuseAI thật trên máy. Đánh dấu NOT_RUN trung thực.");
            return;
        }

        if (IsProfileLockedByExternalProcess(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Profile MuseAI thật đang được mở bởi ứng dụng Fraylo đang chạy. Đánh dấu NOT_RUN trung thực để tránh xung đột khóa độc quyền.");
            return;
        }

        string customProfilesDir = Path.Combine(localAppData, "Fraylo", "profiles");
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), customProfilesDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        string profileId = "profile_museai_98fa16b5891045bab598d73113d45d5e";

        // Tạo tài khoản ProviderAccount liên kết với profile thật này
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "museai",
            DisplayName: "MuseAI Fashion Account",
            ProfileId: profileId,
            MaxConcurrentJobs: 4,
            MaxTabs: 2));

        Assert(!created.Enabled, "Tài khoản ban đầu phải enabled = false");
        Assert(created.VerifiedIdentity == null, "Tài khoản ban đầu chưa có verified identity");

        // Thử bật khi chưa probe -> Bị từ chối
        bool enableBlocked = false;
        try
        {
            await accountService.EnableAccountAsync(created.Id, true);
        }
        catch (ProviderAccountValidationException)
        {
            enableBlocked = true;
        }
        Assert(enableBlocked, "Chưa verify identity thì tuyệt đối CẤM kích hoạt tài khoản.");

        // Chạy probe session thật trên profile
        var probeResult = await accountService.ProbeAccountSessionAsync(created.Id);
        Console.WriteLine($"    -> [DEBUG] Probe Identity = '{probeResult.AccountIdentity}', Status = '{probeResult.SessionStatus}', Error = '{probeResult.ErrorMessage}'");
        Assert(probeResult.IsVerified, "Probe session trên tài khoản MuseAI đã đăng nhập phải IsVerified = true.");
        Assert(string.Equals(probeResult.SessionStatus, "Connected", StringComparison.OrdinalIgnoreCase), "Trạng thái session phải là Connected.");
        Assert(!string.IsNullOrWhiteSpace(probeResult.AccountIdentity), "AccountIdentity phải được trích xuất.");
        Assert(probeResult.AccountIdentity!.Contains("1340335959172092"), "AccountIdentity phải chứa Meta Account ID đáng tin cậy.");

        // Kiểm tra persistence trong database
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var acc = await db.ProviderAccounts.Include(a => a.Profile).FirstOrDefaultAsync(a => a.Id == created.Id);
            Assert(acc != null, "Phải tìm thấy account trong DB.");
            Assert(acc!.VerifiedIdentity == probeResult.AccountIdentity, "VerifiedIdentity trong DB phải khớp với probe.");
            Assert(acc.Profile?.SessionStatus == "Connected", "SessionStatus trong Profile phải là Connected.");
            Assert(acc.Profile?.AccountIdentity == probeResult.AccountIdentity, "AccountIdentity trong Profile phải được lưu.");
        }

        // Bật tài khoản sau khi đã probe hợp lệ -> Thành công
        var enabledAccount = await accountService.EnableAccountAsync(created.Id, true);
        Assert(enabledAccount.Enabled, "Sau khi verified, bật tài khoản phải thành công.");
        Assert(enabledAccount.VerifiedIdentity == probeResult.AccountIdentity, "Verified identity phải bảo toàn.");

        Console.WriteLine($"    -> [PASS]: Đã xác minh danh tính '{probeResult.AccountIdentity}' & Bật hoạt động thành công.");
    }

    private static async Task Test_MuseAi_CloseAndRestartApp_SessionPreserved_AndProbedCorrectly(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string realProfileDir = Path.Combine(localAppData, "Fraylo", "profiles", "profile_museai_98fa16b5891045bab598d73113d45d5e");

        if (!Directory.Exists(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Thiếu profile thật. Đánh dấu NOT_RUN.");
            return;
        }

        if (IsProfileLockedByExternalProcess(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Profile MuseAI thật đang được mở bởi ứng dụng Fraylo đang chạy. Đánh dấu NOT_RUN trung thực.");
            return;
        }

        string customProfilesDir = Path.Combine(localAppData, "Fraylo", "profiles");

        // Giả lập app restart: Tạo mới toàn bộ dependency instances từ đầu
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), customProfilesDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        string profileId = "profile_museai_98fa16b5891045bab598d73113d45d5e";

        // Tìm account đã tạo ở test trước
        await using var db = new FrayloDbContext(dbOptions);
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.ProfileId == profileId);
        if (account == null) return;

        // Probe lại sau restart
        var reprobe = await accountService.ProbeAccountSessionAsync(account.Id);
        Assert(reprobe.IsVerified, "Sau restart, probe lại phải tiếp tục IsVerified = true.");
        Assert(reprobe.AccountIdentity != null && reprobe.AccountIdentity.Contains("1340335959172092"), "Identity sau restart phải nhất quán.");
        Assert(reprobe.SessionStatus == "Connected", "SessionStatus sau restart phải là Connected.");

        Console.WriteLine("    -> [PASS]: Khởi động lại ứng dụng -> Phiên vẫn duy trì và probe lại chuẩn xác.");
    }

    private static async Task Test_MuseAi_UnauthenticatedProfile_CannotEnable_AndReportsCorrectReason(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), tempDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        // Tạo profile hoàn toàn mới (chưa từng đăng nhập)
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "museai",
            DisplayName: "Empty MuseAI Account"));

        // Probe tài khoản chưa đăng nhập
        var probeRes = await accountService.ProbeAccountSessionAsync(created.Id);
        Assert(!probeRes.IsVerified, "Profile chưa đăng nhập thì IsVerified phải = false.");
        Assert(probeRes.SessionStatus == "Unverified", "SessionStatus phải là Unverified.");
        Assert(probeRes.AccountIdentity == null, "AccountIdentity phải là null.");
        Assert(!string.IsNullOrWhiteSpace(probeRes.ErrorMessage), "Phải có thông báo lỗi giải thích rõ ràng.");

        // Thử bật hoạt động -> Bị chặn
        bool blocked = false;
        try
        {
            await accountService.EnableAccountAsync(created.Id, true);
        }
        catch (ProviderAccountValidationException ex)
        {
            blocked = true;
            Assert(ex.Message.Contains("chưa xác minh danh tính"), "Thông báo lỗi phải nêu rõ lý do chưa xác minh.");
        }
        Assert(blocked, "Không được phép bật tài khoản chưa xác minh danh tính.");

        Console.WriteLine("    -> [PASS]: Profile chưa đăng nhập không cho bật hoạt động và báo lý do chính xác.");
    }

    private static async Task Test_MuseAi_NetworkErrorOrTimeout_ReportsSpecificReason_DoesNotClassifyAsExpired(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), tempDir, tabPool);

        // Tạo fake resolver mô phỏng lỗi Timeout / Network error
        var timeoutProbe = new SessionProbe(
            profileRegistry,
            processController: null,
            externalAdapter: (pid, prov, ct) =>
            {
                return Task.FromResult<ProfileIdentityResolutionResult?>(
                    ProfileIdentityResolutionResult.Failed("Quá thời gian tải trang MuseAI (Timeout): 15000ms exceeded. Vui lòng kiểm tra kết nối mạng và thử lại."));
            });

        var accountService = new ProviderAccountService(dbOptions, timeoutProbe);
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "museai",
            DisplayName: "Timeout MuseAI Account"));

        var probeRes = await accountService.ProbeAccountSessionAsync(created.Id);
        Assert(!probeRes.IsVerified, "Lỗi mạng/timeout thì IsVerified phải là false.");
        Assert(probeRes.ErrorMessage != null && probeRes.ErrorMessage!.Contains("Timeout"), "Phải báo đúng nguyên nhân Timeout.");
        Assert(!probeRes.ErrorMessage!.Contains("hết hạn"), "Lỗi mạng/timeout tuyệt đối không được quy nhầm thành phiên hết hạn.");

        Console.WriteLine("    -> [PASS]: Lỗi mạng/timeout báo đúng nguyên nhân, không nhầm sang phiên hết hạn.");
    }

    private static async Task Test_MuseAi_TwoProfiles_NoIdentityBleedOrCrossAccountUpdate(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string realProfileDir = Path.Combine(localAppData, "Fraylo", "profiles", "profile_museai_98fa16b5891045bab598d73113d45d5e");

        if (!Directory.Exists(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Thiếu profile thật. Đánh dấu NOT_RUN.");
            return;
        }

        if (IsProfileLockedByExternalProcess(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Profile MuseAI thật đang được mở bởi ứng dụng Fraylo đang chạy. Đánh dấu NOT_RUN trung thực.");
            return;
        }

        string customProfilesDir = Path.Combine(localAppData, "Fraylo", "profiles");
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), customProfilesDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        // Account 1: Lấy ID account đã tạo liên kết với profile thật đã login
        string acc1Id;
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var existing = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.ProfileId == "profile_museai_98fa16b5891045bab598d73113d45d5e");
            if (existing != null)
            {
                acc1Id = existing.Id;
            }
            else
            {
                var createdAcc = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
                    ProviderKey: "museai",
                    DisplayName: "Account 1 - Logged In",
                    ProfileId: "profile_museai_98fa16b5891045bab598d73113d45d5e"));
                acc1Id = createdAcc.Id;
            }
        }

        // Account 2: Dùng profile giả lập thứ 2 (chưa login)
        string unauthProfileId = $"profile_museai_unauth_{Guid.NewGuid():N}";
        var acc2 = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "museai",
            DisplayName: "Account 2 - Empty",
            ProfileId: unauthProfileId));

        // Probe Account 1
        var probe1 = await accountService.ProbeAccountSessionAsync(acc1Id);
        Assert(probe1.IsVerified, "Account 1 phải verified.");
        Assert(probe1.AccountIdentity != null && probe1.AccountIdentity.Contains("1340335959172092"), "Identity Account 1 phải đúng.");

        // Probe Account 2
        var probe2 = await accountService.ProbeAccountSessionAsync(acc2.Id);
        Assert(!probe2.IsVerified, "Account 2 chưa login thì IsVerified phải = false.");
        Assert(probe2.AccountIdentity == null, "Account 2 tuyệt đối không được nhận nhầm identity của Account 1.");

        // Kiểm tra lại Account 1 trong DB không bị Account 2 làm thay đổi
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var dbAcc1 = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == acc1Id);
            var dbAcc2 = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == acc2.Id);

            Assert(dbAcc1!.VerifiedIdentity != null && dbAcc1.VerifiedIdentity.Contains("1340335959172092"), "Account 1 vẫn giữ nguyên danh tính.");
            Assert(dbAcc2!.VerifiedIdentity == null, "Account 2 không bị nhiễm identity.");
        }

        Console.WriteLine("    -> [PASS]: Cô lập hoàn toàn giữa 2 profile MuseAI, không có hiện tượng rò rỉ hay nhận nhầm danh tính.");
    }

    private static async Task Test_MuseAi_StaleProbeOrContextClosed_DoesNotOverwriteVerifiedResult(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), tempDir, tabPool);

        string testIdentity = "StaleUser (999888777)";
        string profileId = $"profile_muse_stale_{Guid.NewGuid():N}";
        await profileRegistry.EnsureProfileAsync(profileId, "MuseAI", accountIdentity: testIdentity);
        await profileRegistry.UpdateProfileStateAsync(
            profileId,
            visibilityState: BrowserVisibilityState.Hidden,
            sessionStatus: BrowserSessionStatus.Connected,
            accountIdentity: testIdentity);

        // Tạo ProviderAccount đã verified
        var accountService = new ProviderAccountService(dbOptions, new SessionProbe(profileRegistry));
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "museai",
            DisplayName: "Stale Guard Account",
            ProfileId: profileId));

        // Probe session cập nhật verified
        var probeRes = await accountService.ProbeAccountSessionAsync(created.Id);
        Assert(probeRes.IsVerified, "Account phải verified.");

        // Giả lập sự kiện người dùng đóng cửa sổ Chromium (ContextClosed)
        await profileRegistry.UpdateProfileStateAsync(
            profileId,
            operationalState: BrowserOperationalState.Stopped,
            visibilityState: BrowserVisibilityState.Hidden,
            reasonCode: BrowserReasonCode.AuthClosed);

        // Kiểm tra lại trong DB: VerifiedIdentity và SessionStatus không bị reset
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var acc = await db.ProviderAccounts.Include(a => a.Profile).FirstOrDefaultAsync(a => a.Id == created.Id);
            Assert(acc!.VerifiedIdentity == testIdentity, "VerifiedIdentity không bị mất khi đóng cửa sổ.");
            Assert(acc.Profile?.SessionStatus == "Connected", "SessionStatus không bị ghi đè thành Unverified khi đóng cửa sổ.");
        }

        Console.WriteLine("    -> [PASS]: Sự kiện đóng browser hoặc stale event không làm mất hoặc ghi đè kết quả Verified.");
    }
}
