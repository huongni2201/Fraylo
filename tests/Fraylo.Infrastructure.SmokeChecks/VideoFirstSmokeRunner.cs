using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Infrastructure.SmokeChecks;

public static class VideoFirstSmokeRunner
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--emit-final-schema", StringComparison.OrdinalIgnoreCase))
        {
            var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite("Data Source=:memory:").Options;
            await using var db = new FrayloDbContext(options);
            await File.WriteAllTextAsync(args[1], db.Database.GenerateCreateScript() + Environment.NewLine + "PRAGMA user_version = 8;" + Environment.NewLine);
            return 0;
        }

        var directory = Path.Combine(Path.GetTempPath(), "Fraylo_infrastructure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "smoke.db");
        try
        {
            var result = await new SqliteDatabaseBootstrapper().BootstrapAsync(database);
            if (!result.IsBootstrapSuccessful)
            {
                throw new InvalidOperationException("Không thể khởi tạo database kiểm thử.");
            }

            await ThemePreferenceStoreSmokeChecks.VerifyAsync(database);
            await ProjectAndSceneSmokeTests.RunTestsAsync(database);
            await StorageAndAssetSmokeTests.RunTestsAsync(database);
            Console.WriteLine("Các kiểm tra Infrastructure video-first đã hoàn tất.");
            return 0;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Không thể dọn fixture: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Không thể dọn fixture: {ex.Message}");
            }
        }
    }
}
