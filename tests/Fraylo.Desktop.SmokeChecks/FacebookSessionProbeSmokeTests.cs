using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Accounts;
using Fraylo.Browser;
using Fraylo.Browser.Models;
using Fraylo.Browser.Probers;
using Fraylo.Contracts.Accounts;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Giai đoạn C1 của FACEBOOK_PUBLISHING_PLAN: kind/provider Facebook, URL mặc định và đọc danh tính từ cookie.
/// Không mở browser, không liên hệ Facebook thật (probe thật là NOT_RUN).
/// </summary>
public static class FacebookSessionProbeSmokeTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"[ASSERTION FAILED]: {message}");
    }

    public static async Task<int> RunAllAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Fraylo_facebook_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert(FacebookPlatformProber.ReadIdentity([("c_user", "100012345"), ("xs", "token")]) == "Facebook (100012345)",
                "c_user + xs hợp lệ phải ra danh tính Facebook (ID)");
            Assert(FacebookPlatformProber.ReadIdentity([("c_user", "100012345")]) == null, "Thiếu xs không được coi là đã đăng nhập");
            Assert(FacebookPlatformProber.ReadIdentity([("xs", "token")]) == null, "Thiếu c_user không được coi là đã đăng nhập");
            Assert(FacebookPlatformProber.ReadIdentity([("c_user", "abc"), ("xs", "token")]) == null, "c_user không phải số bị từ chối");
            Assert(FacebookPlatformProber.ReadIdentity([("c_user", ""), ("xs", "token")]) == null, "c_user rỗng bị từ chối");

            Assert(FacebookPlatformProber.IsLoginOrCheckpointUrl("https://www.facebook.com/login/?next=x"), "/login phải bị nhận diện");
            Assert(FacebookPlatformProber.IsLoginOrCheckpointUrl("https://www.facebook.com/checkpoint/1234"), "/checkpoint phải bị nhận diện");
            Assert(!FacebookPlatformProber.IsLoginOrCheckpointUrl("https://www.facebook.com/"), "Trang chủ không phải login");

            Assert(ProviderUrlResolver.ResolveUrl("facebook") == ProviderUrlResolver.DefaultFacebookUrl, "providerKey facebook -> URL Facebook");
            Assert(ProviderUrlResolver.ResolveUrl(null, "Facebook") == ProviderUrlResolver.DefaultFacebookUrl, "kind Facebook -> URL Facebook");
            Assert(BrowserProfileKind.IsValid("Facebook"), "kind Facebook hợp lệ");

            var dbPath = Path.Combine(tempDir, "facebook_smoke.db");
            await new SqliteDatabaseBootstrapper().BootstrapAsync(dbPath);
            var dbOptions = new DbContextOptionsBuilder<FrayloDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString())
                .Options;
            var service = new ProviderAccountService(dbOptions, new NoopResolver());

            var created = await service.CreateAccountAsync(new CreateProviderAccountRequest(
                ProviderKey: "Facebook", DisplayName: "Test FB", MaxConcurrentJobs: 1, MaxTabs: 1));
            Assert(created.ProviderKey == "facebook", "providerKey được chuẩn hóa thành facebook");
            Assert(!created.Enabled && created.VerifiedIdentity == null, "Tài khoản mới chưa bật, chưa có danh tính");
            await using (var db = new FrayloDbContext(dbOptions))
            {
                var profile = await db.BrowserProfiles.AsNoTracking().SingleAsync();
                Assert(profile.Kind == BrowserProfileKind.Facebook, "Profile tạo kèm phải có kind Facebook");
                Assert(profile.SessionStatus == BrowserSessionStatus.Unverified, "Profile mới chưa xác minh");
            }

            var rejected = false;
            try
            {
                await service.CreateAccountAsync(new CreateProviderAccountRequest(ProviderKey: "instagram", DisplayName: "x"));
            }
            catch (ProviderAccountValidationException) { rejected = true; }
            Assert(rejected, "Provider ngoài danh sách vẫn bị từ chối");

            Console.WriteLine("Facebook C1 smoke: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Facebook C1 smoke: FAIL - {ex.Message}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class NoopResolver : IProfileIdentityResolver
    {
        public Task<ProfileIdentityResolutionResult> ResolveProfileIdentityAsync(
            string profileId, string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProfileIdentityResolutionResult.Failed("không probe trong smoke"));
    }
}
