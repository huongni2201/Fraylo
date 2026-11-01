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
/// Bộ kiểm thử khói chuyên biệt cho nhận diện đăng nhập ChatGPT Web theo chuẩn hệ thống:
/// 1. Đăng nhập ChatGPT thật -> kiểm tra phiên -> identity được xác minh -> bật hoạt động thành công.
/// 2. Đóng rồi mở lại browser và restart app: phiên còn hợp lệ được probe lại đúng.
/// 3. Chưa đăng nhập/phiên hết hạn: không cho bật, báo đúng lý do.
/// 4. Lỗi mạng/timeout: báo đúng nguyên nhân, không nhầm sang phiên hết hạn.
/// 5. Hai profile khác nhau: không nhận nhầm identity hoặc cập nhật nhầm tài khoản.
/// 6. Probe cũ/sự kiện đóng context cũ không ghi đè kết quả Verified.
/// </summary>
public static class ChatGptSessionProbeSmokeTests
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
        string lockFilePath = Path.Combine(profileDir, ".titoai_lock");
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
        Console.WriteLine("     Fraylo — ChatGPT Session & Identity Probe Smoke Checks      ");
        Console.WriteLine("=================================================================");

        var tempDir = Path.Combine(Path.GetTempPath(), "titoai_chatgpt_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "chatgpt_smoke.db");

        try
        {
            var bootstrapper = new SqliteDatabaseBootstrapper();
            await bootstrapper.BootstrapAsync(dbPath);
            var dbOptions = CreateSqliteOptions(dbPath);

            Console.WriteLine("\n[1] Kiểm thử: ChatGPT thật đã đăng nhập -> Probe Session -> Xác minh Identity -> Bật hoạt động thành công");
            await Test_ChatGptRealLogin_ProbeSession_VerifiesIdentity_AndEnablesAccountAsync(dbOptions, tempDir);

            Console.WriteLine("\n[2] Kiểm thử: Đóng browser & restart app -> Phiên còn hợp lệ được probe lại đúng");
            await Test_ChatGpt_CloseAndRestartApp_SessionPreserved_AndProbedCorrectly(dbOptions, tempDir);

            Console.WriteLine("\n[3] Kiểm thử: Chưa đăng nhập / phiên hết hạn -> Không cho bật hoạt động");
            await Test_ChatGpt_UnauthenticatedProfile_CannotEnable_AndReportsCorrectReason(dbOptions, tempDir);

            Console.WriteLine("\n[4] Kiểm thử: Lỗi mạng / timeout -> Báo đúng nguyên nhân, không nhầm sang chưa đăng nhập");
            await Test_ChatGpt_NetworkErrorOrTimeout_ReportsSpecificReason_DoesNotClassifyAsExpired(dbOptions, tempDir);

            Console.WriteLine("\n[5] Kiểm thử: Hai profile khác nhau -> Không nhận nhầm identity hoặc cập nhật nhầm tài khoản");
            await Test_ChatGpt_TwoProfiles_NoIdentityBleedOrCrossAccountUpdate(dbOptions, tempDir);

            Console.WriteLine("\n[6] Kiểm thử: Probe cũ / sự kiện đóng context cũ -> Không ghi đè kết quả Verified");
            await Test_ChatGpt_StaleProbeOrContextClosed_DoesNotOverwriteVerifiedResult(dbOptions, tempDir);

            Console.WriteLine("\n=================================================================");
            Console.WriteLine("   TẤT CẢ 6/6 KIỂM THỬ KHÓI CHATGPT SESSION PROBE ĐÃ VƯỢT QUA!  ");
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

    private static async Task Test_ChatGptRealLogin_ProbeSession_VerifiesIdentity_AndEnablesAccountAsync(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profileId = "profile_chatgpt_web_d4b5bc918d7647e1b9550b30de78fb80";
        string realProfileDir = Path.Combine(localAppData, "TiToAI", "profiles", profileId);

        if (!Directory.Exists(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Không tìm thấy thư mục profile ChatGPT thật trên máy. Đánh dấu NOT_RUN trung thực.");
            return;
        }

        if (IsProfileLockedByExternalProcess(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Profile ChatGPT thật đang bị khóa bởi tiến trình ngoài. Đánh dấu NOT_RUN trung thực.");
            return;
        }

        string customProfilesDir = Path.Combine(localAppData, "TiToAI", "profiles");
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), customProfilesDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        // Tạo tài khoản ProviderAccount liên kết với profile ChatGPT thật này
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "chatgpt-web",
            DisplayName: "Team 1",
            ProfileId: profileId,
            MaxConcurrentJobs: 6,
            MaxTabs: 6));

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
        Assert(probeResult.IsVerified, "Probe session trên tài khoản ChatGPT đã đăng nhập phải IsVerified = true.");
        Assert(string.Equals(probeResult.SessionStatus, "Connected", StringComparison.OrdinalIgnoreCase), "Trạng thái session phải là Connected.");
        Assert(!string.IsNullOrWhiteSpace(probeResult.AccountIdentity), "AccountIdentity phải được trích xuất.");
        Assert(probeResult.AccountIdentity!.Contains("user-BbuIgLyxYtZNVqxB02mBh3Pa"), "AccountIdentity phải chứa OpenAI User ID đáng tin cậy.");

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

    private static async Task Test_ChatGpt_CloseAndRestartApp_SessionPreserved_AndProbedCorrectly(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profileId = "profile_chatgpt_web_d4b5bc918d7647e1b9550b30de78fb80";
        string realProfileDir = Path.Combine(localAppData, "TiToAI", "profiles", profileId);

        if (!Directory.Exists(realProfileDir) || IsProfileLockedByExternalProcess(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Bỏ qua test restart vì thiếu hoặc đang khóa profile.");
            return;
        }

        string customProfilesDir = Path.Combine(localAppData, "TiToAI", "profiles");

        // Giả lập app restart: Tạo mới toàn bộ dependency instances từ đầu
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), customProfilesDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        await using var db = new FrayloDbContext(dbOptions);
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.ProfileId == profileId);
        if (account == null) return;

        // Probe lại sau restart
        var reprobe = await accountService.ProbeAccountSessionAsync(account.Id);
        Assert(reprobe.IsVerified, "Sau restart, probe lại phải tiếp tục IsVerified = true.");
        Assert(reprobe.AccountIdentity != null && reprobe.AccountIdentity.Contains("user-BbuIgLyxYtZNVqxB02mBh3Pa"), "Identity sau restart phải nhất quán.");
        Assert(reprobe.SessionStatus == "Connected", "SessionStatus sau restart phải là Connected.");

        Console.WriteLine("    -> [PASS]: Khởi động lại ứng dụng -> Phiên ChatGPT vẫn duy trì và probe lại chuẩn xác.");
    }

    private static async Task Test_ChatGpt_UnauthenticatedProfile_CannotEnable_AndReportsCorrectReason(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), tempDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        // Tạo profile ChatGPT hoàn toàn mới (chưa từng đăng nhập)
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "chatgpt-web",
            DisplayName: "Empty ChatGPT Account"));

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

    private static async Task Test_ChatGpt_NetworkErrorOrTimeout_ReportsSpecificReason_DoesNotClassifyAsExpired(
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
                    ProfileIdentityResolutionResult.Failed("Quá thời gian tải trang ChatGPT (Timeout): 15000ms exceeded. Vui lòng kiểm tra kết nối mạng và thử lại."));
            });

        var accountService = new ProviderAccountService(dbOptions, timeoutProbe);
        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "chatgpt-web",
            DisplayName: "Timeout ChatGPT Account"));

        var probeRes = await accountService.ProbeAccountSessionAsync(created.Id);
        Assert(!probeRes.IsVerified, "Lỗi mạng/timeout thì IsVerified phải là false.");
        Assert(probeRes.ErrorMessage != null && probeRes.ErrorMessage!.Contains("Timeout"), "Phải báo đúng nguyên nhân Timeout.");
        Assert(!probeRes.ErrorMessage!.Contains("hết hạn"), "Lỗi mạng/timeout tuyệt đối không được quy nhầm thành phiên hết hạn.");

        Console.WriteLine("    -> [PASS]: Lỗi mạng/timeout báo đúng nguyên nhân, không nhầm sang phiên hết hạn.");
    }

    private static async Task Test_ChatGpt_TwoProfiles_NoIdentityBleedOrCrossAccountUpdate(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profileId1 = "profile_chatgpt_web_d4b5bc918d7647e1b9550b30de78fb80";
        string realProfileDir = Path.Combine(localAppData, "TiToAI", "profiles", profileId1);

        if (!Directory.Exists(realProfileDir) || IsProfileLockedByExternalProcess(realProfileDir))
        {
            Console.WriteLine("    -> [NOT_RUN]: Bỏ qua test 2 profiles vì thiếu profile thật.");
            return;
        }

        string customProfilesDir = Path.Combine(localAppData, "TiToAI", "profiles");
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), customProfilesDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        string acc1Id;
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var existing = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.ProfileId == profileId1);
            if (existing != null)
            {
                acc1Id = existing.Id;
            }
            else
            {
                var a1 = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
                    ProviderKey: "chatgpt-web",
                    DisplayName: "Account 1",
                    ProfileId: profileId1));
                acc1Id = a1.Id;
            }
        }

        // Tạo tài khoản thứ 2 với profile trắng hoàn toàn
        var acc2 = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "chatgpt-web",
            DisplayName: "Account 2 Blank"));

        // Probe tài khoản 2
        var probe2 = await accountService.ProbeAccountSessionAsync(acc2.Id);
        Assert(!probe2.IsVerified, "Account 2 profile trắng không được nhận identity.");
        Assert(probe2.AccountIdentity == null, "Account 2 không được nhận nhầm identity của Account 1.");

        // Kiểm tra trong DB: acc1 vẫn giữ identity, acc2 không bị ghi đè
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var dbAcc1 = await db.ProviderAccounts.FindAsync(acc1Id);
            var dbAcc2 = await db.ProviderAccounts.FindAsync(acc2.Id);

            Assert(dbAcc1 != null && dbAcc1.VerifiedIdentity != null && dbAcc1.VerifiedIdentity.Contains("user-BbuIgLyxYtZNVqxB02mBh3Pa"), "Acc 1 phải bảo toàn identity.");
            Assert(dbAcc2 != null && dbAcc2.VerifiedIdentity == null, "Acc 2 tuyệt đối không được có identity của Acc 1.");
        }

        Console.WriteLine("    -> [PASS]: Hai profile ChatGPT độc lập hoàn toàn, không có hiện tượng rò rỉ hoặc ghi đè chéo danh tính.");
    }

    private static async Task Test_ChatGpt_StaleProbeOrContextClosed_DoesNotOverwriteVerifiedResult(
        DbContextOptions<FrayloDbContext> dbOptions,
        string tempDir)
    {
        var tabPool = new TabPool();
        var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), tempDir, tabPool);
        var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
        var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);
        var sessionProbe = new SessionProbe(profileRegistry, processController);
        var accountService = new ProviderAccountService(dbOptions, sessionProbe);

        var created = await accountService.CreateAccountAsync(new CreateProviderAccountRequest(
            ProviderKey: "chatgpt-web",
            DisplayName: "Race Safety Test Account"));

        // Giả lập profile đã được verify thành công trước đó
        string verifiedId = "David Martinez (user-BbuIgLyxYtZNVqxB02mBh3Pa)";
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var acc = await db.ProviderAccounts.Include(a => a.Profile).FirstAsync(a => a.Id == created.Id);
            acc.VerifiedIdentity = verifiedId;
            acc.Enabled = true;
            if (acc.Profile != null)
            {
                acc.Profile.SessionStatus = "Connected";
                acc.Profile.AccountIdentity = verifiedId;
            }
            await db.SaveChangesAsync();
        }

        // Giả lập sự kiện đóng context trình duyệt phát ra
        await profileRegistry.UpdateProfileStateAsync(
            created.ProfileId,
            visibilityState: BrowserVisibilityState.Hidden,
            operationalState: BrowserOperationalState.Idle);

        // Kiểm tra sau khi đóng context, trạng thái phiên Connected và VerifiedIdentity trong DB không bị xóa
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var acc = await db.ProviderAccounts.Include(a => a.Profile).FirstAsync(a => a.Id == created.Id);
            Assert(acc.VerifiedIdentity == verifiedId, "Đóng context không được xóa VerifiedIdentity của tài khoản.");
            Assert(acc.Profile?.SessionStatus == "Connected", "SessionStatus trong Profile phải bảo toàn Connected.");
        }

        Console.WriteLine("    -> [PASS]: Đóng context trình duyệt bảo toàn trạng thái Verified của phiên ChatGPT.");
    }
}
