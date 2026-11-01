using Fraylo.Infrastructure.Settings;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Infrastructure.SmokeChecks;

public static class ThemePreferenceStoreSmokeChecks
{
    public static async Task VerifyAsync(string databasePath)
    {
        var databaseDirectory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("Theme preference test database must have a parent directory.");
        var themeDatabasePath = Path.Combine(databaseDirectory, "theme-preference-smoke.db");
        var bootstrapResult = await new SqliteDatabaseBootstrapper().BootstrapAsync(themeDatabasePath);
        Assert(bootstrapResult.IsBootstrapSuccessful, "Theme preference test database bootstrap should succeed.");

        var store = new ThemePreferenceStore(themeDatabasePath);
        Assert(await store.GetAsync() == null, "Missing theme preference should remain unset.");

        await store.SetAsync("dark");
        Assert(await store.GetAsync() == "dark", "Theme preference should persist as dark.");

        await store.SetAsync("light");
        Assert(await store.GetAsync() == "light", "Saving again should update the existing key.");

        bool invalidValueRejected = false;
        try
        {
            await store.SetAsync("sepia");
        }
        catch (ArgumentException)
        {
            invalidValueRejected = true;
        }

        Assert(invalidValueRejected, "Unsupported theme preference should be rejected.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
