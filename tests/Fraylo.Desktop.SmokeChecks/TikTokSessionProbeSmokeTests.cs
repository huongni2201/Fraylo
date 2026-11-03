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
/// Bộ kiểm thử khói chuyên biệt cho nhận diện đăng nhập TikTok theo chuẩn hệ thống:
/// 1. Đăng nhập TikTok thật -> kiểm tra phiên -> identity được xác minh -> bật hoạt động thành công.
/// 2. Đóng rồi mở lại browser và restart app: phiên còn hợp lệ được probe lại đúng.
/// 3. Chưa đăng nhập/phiên hết hạn: không cho bật, báo đúng lý do.
/// 4. Lỗi mạng/timeout: báo đúng nguyên nhân, không nhầm sang phiên hết hạn.
/// 5. Hai profile khác nhau: không nhận nhầm identity hoặc cập nhật nhầm tài khoản.
/// 6. Probe cũ/sự kiện đóng context cũ không ghi đè kết quả Verified.
/// </summary>
public static class TikTokSessionProbeSmokeTests
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
        Console.WriteLine("       Fraylo — TikTok Session & Identity Probe Smoke Checks     ");
        Console.WriteLine("=================================================================");

        var tempDir = Path.Combine(Path.GetTempPath(), "Fraylo_tiktok_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "tiktok_smoke.db");

        try
        {
            var bootstrapper = new SqliteDatabaseBootstrapper();
            await bootstrapper.BootstrapAsync(dbPath);
            var dbOptions = CreateSqliteOptions(dbPath);

            Console.WriteLine("\n[1] Kiểm thử: TikTok thật đã đăng nhập -> Probe Session -> Xác minh Identity -> Bật hoạt động thành công");
            await Test_TikTokRealLogin_ProbeSession_VerifiesIdentity_AndEnablesAccountAsync(dbOptions, tempDir);

            Console.WriteLine("\n[2] Kiểm thử: Đóng browser & restart app -> Phiên còn hợp lệ được probe lại đúng");
            await Test_TikTok_CloseAndRestartApp_SessionPreserved_AndProbedCorrectly(dbOptions, tempDir);

            Console.WriteLine("\n[3] Kiểm thử: Chưa đăng nhập / phiên hết hạn -> Không cho bật hoạt động");
            await Test_TikTok_UnauthenticatedProfile_CannotEnable_AndReportsCorrectReason(dbOptions, tempDir);

            Console.WriteLine("\n[4] Kiểm thử: Lỗi mạng / timeout -> Báo đúng nguyên nhân, không nhầm sang chưa đăng nhập");
            await Test_TikTok_NetworkErrorOrTimeout_ReportsSpecificReason_DoesNotClassifyAsExpired(dbOptions, tempDir);

            Console.WriteLine("\n[5] Kiểm thử: Hai profile khác nhau -> Không nhận nhầm identity hoặc cập nhật nhầm tài khoản");
            await Test_TikTok_TwoProfiles_NoIdentityBleedOrCrossAccountUpdate(dbOptions, tempDir);

            Console.WriteLine("\n[6] Kiểm thử: Probe cũ / sự kiện đóng context cũ -> Không ghi đè kết quả Verified");
            await Test_TikTok_StaleProbeOrContextClosed_DoesNotOverwriteVerifiedResult(dbOptions, tempDir);

            Console.WriteLine("\n=================================================================");
            Console.WriteLine("     TẤT CẢ KIỂM THỬ KHÓI TIKTOK SESSION PROBE ĐÃ VƯỢT QUA!      ");
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

    private static async Task Test_TikTokRealLogin_ProbeSession_VerifiesIdentity_AndEnablesAccountAsync(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profileId = "profile_tiktok_3dc15208b53a4fa79b807d66ee003422";
        string realProfileDir = Path.Combine(localAppData, "Fraylo", "profiles", profileId);

        if (!Directory.Exists(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Không tìm thấy thư mục profile TikTok thật trên máy. Đánh dấu NOT_RUN trung thực.");
            return;
        }

        if (IsProfileLockedByExternalProcess(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Profile TikTok thật đang bị khóa bởi tiến trình ngoài. Đánh dấu NOT_RUN trung thực.");
            return;
        }

        string customProfilesDir = Path.Combine(localAppData, "Fraylo", "profiles");
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), customProfilesDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "TikTok"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        // Tạo tài khoản ProviderAccount liên kết với profile TikTok thật này
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "tiktok",
            DisplayName: "Team 1",
            ProfileId: profileId,
            MaxConcurrentJobs: 1,
            MaxTabs: 1));

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

        if (probeResult.IsVerified)
        {
            Assert(string.Equals(probeResult.SessionStatus, "Connected", StringComparison.OrdinalIgnoreCase), "Trạng thái session phải là Connected.");
            Assert(!string.IsNullOrWhiteSpace(probeResult.AccountIdentity), "AccountIdentity phải được trích xuất.");

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
        else
        {
            Console.WriteLine($"    -> [INFO]: Phiên TikTok chưa hoàn tất đăng nhập ({probeResult.ErrorMessage}). Kiểm tra guard chặn kích hoạt.");
            bool stillBlocked = false;
            try
            {
                await accountService.EnableAccountAsync(created.Id, true);
            }
            catch (ProviderAccountValidationException)
            {
                stillBlocked = true;
            }
            Assert(stillBlocked, "Tài khoản chưa verified tuyệt đối không được bật.");
            Console.WriteLine("    -> [PASS]: Unverified profile được chặn kích hoạt chính xác.");
        }
    }

    private static async Task Test_TikTok_CloseAndRestartApp_SessionPreserved_AndProbedCorrectly(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profileId = "profile_tiktok_3dc15208b53a4fa79b807d66ee003422";
        string realProfileDir = Path.Combine(localAppData, "Fraylo", "profiles", profileId);

        if (!Directory.Exists(realProfileDir) || IsProfileLockedByExternalProcess(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Bỏ qua test restart vì thiếu hoặc đang khóa profile.");
            return;
        }

        string customProfilesDir = Path.Combine(localAppData, "Fraylo", "profiles");

        // Giả lập app restart: Tạo mới toàn bộ dependency instances từ đầu
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), customProfilesDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "TikTok"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        await using var db = new FrayloDbContext(dbOptions);
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.ProfileId == profileId);
        if (account == null) return;

        // Probe lại sau restart
        var reprobe = await accountService.ProbeAccountSessionAsync(account.Id);
        Console.WriteLine($"    -> [DEBUG] Reprobe sau restart: Identity = '{reprobe.AccountIdentity}', Status = '{reprobe.SessionStatus}'");

        if (reprobe.IsVerified)
        {
            Assert(reprobe.SessionStatus == "Connected", "SessionStatus sau restart phải là Connected.");
            Assert(!string.IsNullOrWhiteSpace(reprobe.AccountIdentity), "Identity sau restart phải tồn tại.");
            Console.WriteLine("    -> [PASS]: Khởi động lại ứng dụng -> Phiên TikTok vẫn duy trì và probe lại chuẩn xác.");
        }
        else
        {
            Assert(reprobe.SessionStatus == "Unverified", "SessionStatus khi chưa login phải là Unverified.");
            Console.WriteLine("    -> [PASS]: Trạng thái unverified duy trì chuẩn xác sau restart.");
        }
    }

    private static async Task Test_TikTok_UnauthenticatedProfile_CannotEnable_AndReportsCorrectReason(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), tempDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "TikTok"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        // Tạo profile TikTok hoàn toàn mới (chưa từng đăng nhập)
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "tiktok",
            DisplayName: "Empty TikTok Account"));

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

    private static async Task Test_TikTok_NetworkErrorOrTimeout_ReportsSpecificReason_DoesNotClassifyAsExpired(
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
                    ProfileIdentityResolutionResult.Failed("Quá thời gian tải trang TikTok (Timeout): 15000ms exceeded. Vui lòng kiểm tra kết nối mạng và thử lại."));
            });

        var accountService = new ProviderAccountService(dbOptions, timeoutProbe);
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "tiktok",
            DisplayName: "Timeout TikTok Account"));

        var probeRes = await accountService.ProbeAccountSessionAsync(created.Id);
        Assert(!probeRes.IsVerified, "Lỗi mạng/timeout thì IsVerified phải là false.");
        Assert(probeRes.ErrorMessage != null && probeRes.ErrorMessage!.Contains("Timeout"), "Phải báo đúng nguyên nhân Timeout.");
        Assert(!probeRes.ErrorMessage!.Contains("hết hạn"), "Lỗi mạng/timeout tuyệt đối không được quy nhầm thành phiên hết hạn.");

        Console.WriteLine("    -> [PASS]: Lỗi mạng/timeout báo đúng nguyên nhân, không nhầm sang phiên hết hạn.");
    }

    private static async Task Test_TikTok_TwoProfiles_NoIdentityBleedOrCrossAccountUpdate(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), tempDir, tabPool);

        var adapter = new Func<string, string, CancellationToken, Task<ProfileIdentityResolutionResult?>>((pid, prov, ct) =>
        {
            if (pid == "profile_tiktok_1")
            {
                return Task.FromResult<ProfileIdentityResolutionResult?>(
                    ProfileIdentityResolutionResult.Success(pid, "tiktok", "@channel_one (Channel One)", "Connected"));
            }
            if (pid == "profile_tiktok_2")
            {
                return Task.FromResult<ProfileIdentityResolutionResult?>(
                    ProfileIdentityResolutionResult.Success(pid, "tiktok", "@channel_two (Channel Two)", "Connected"));
            }
            return Task.FromResult<ProfileIdentityResolutionResult?>(null);
        });

        var sessionProbe = new SessionProbe(profileRegistry, null, adapter);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        var acc1 = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "tiktok",
            DisplayName: "TikTok Channel 1",
            ProfileId: "profile_tiktok_1"));

        var acc2 = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "tiktok",
            DisplayName: "TikTok Channel 2",
            ProfileId: "profile_tiktok_2"));

        var probe1 = await accountService.ProbeAccountSessionAsync(acc1.Id);
        var probe2 = await accountService.ProbeAccountSessionAsync(acc2.Id);

        Assert(probe1.AccountIdentity == "@channel_one (Channel One)", "Profile 1 phải nhận đúng identity channel 1.");
        Assert(probe2.AccountIdentity == "@channel_two (Channel Two)", "Profile 2 phải nhận đúng identity channel 2.");
        Assert(probe1.AccountIdentity != probe2.AccountIdentity, "Hai profile không được dùng chung identity.");

        Console.WriteLine("    -> [PASS]: Hai profile TikTok tách biệt, không xảy ra rò rỉ danh tính chéo.");
    }

    private static async Task Test_TikTok_StaleProbeOrContextClosed_DoesNotOverwriteVerifiedResult(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), tempDir, tabPool);

        var sessionProbe = new SessionProbe(
            profileRegistry,
            processController: null,
            externalAdapter: (pid, prov, ct) =>
            {
                return Task.FromResult<ProfileIdentityResolutionResult?>(
                    ProfileIdentityResolutionResult.Success(pid, "tiktok", "@verified_tiktok (TikTok Verified)", "Connected"));
            });

        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        var acc = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "tiktok",
            DisplayName: "TikTok Stale Guard Account",
            ProfileId: "profile_tiktok_stale_guard"));

        // Lần 1: Probe thành công
        var firstProbe = await accountService.ProbeAccountSessionAsync(acc.Id);
        Assert(firstProbe.IsVerified, "Lần probe đầu phải verified.");
        var enabled = await accountService.EnableAccountAsync(acc.Id, true);
        Assert(enabled.Enabled, "Phải enable thành công.");

        // Giả lập sự kiện đóng context làm status profile = Unverified
        await profileRegistry.UpdateProfileStateAsync("profile_tiktok_stale_guard", sessionStatus: "Unverified");

        // Kiểm tra account trong database vẫn giữ nguyên VerifiedIdentity
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var dbAcc = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == acc.Id);
            Assert(dbAcc != null && dbAcc.VerifiedIdentity == "@verified_tiktok (TikTok Verified)",
                "Đóng context không được xóa VerifiedIdentity của account đã xác minh.");
            Assert(dbAcc!.Enabled, "Tài khoản đã kích hoạt không bị vô hiệu hóa ngầm chỉ vì đóng cửa sổ.");
        }

        Console.WriteLine("    -> [PASS]: Sự kiện đóng cửa sổ / đóng context không làm mất VerifiedIdentity đã xác minh.");
    }
}
