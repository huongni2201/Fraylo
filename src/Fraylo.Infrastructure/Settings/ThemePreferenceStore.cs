using Microsoft.EntityFrameworkCore;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;

namespace Fraylo.Infrastructure.Settings;

public sealed class ThemePreferenceStore
{
    public const string SettingKey = "ui.theme";

    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ThemePreferenceStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var optionsBuilder = new DbContextOptionsBuilder<FrayloDbContext>();
        optionsBuilder.UseSqlite(DatabasePathHelper.BuildConnectionString(databasePath))
                      .AddInterceptors(new SqlitePragmaInterceptor());
        _dbOptions = optionsBuilder.Options;
    }

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = new FrayloDbContext(_dbOptions);
        return await dbContext.AppSettings
            .AsNoTracking()
            .Where(setting => setting.Key == SettingKey)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetAsync(string preference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preference);
        if (!IsValidPreference(preference))
        {
            throw new ArgumentException("Theme preference must be system, light, or dark.", nameof(preference));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = new FrayloDbContext(_dbOptions);
            var setting = await dbContext.AppSettings
                .SingleOrDefaultAsync(item => item.Key == SettingKey, cancellationToken)
                .ConfigureAwait(false);

            if (setting == null)
            {
                dbContext.AppSettings.Add(new AppSetting
                {
                    Key = SettingKey,
                    Value = preference,
                    UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                });
            }
            else
            {
                setting.Value = preference;
                setting.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static bool IsValidPreference(string preference)
    {
        return string.Equals(preference, "system", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(preference, "light", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(preference, "dark", StringComparison.OrdinalIgnoreCase);
    }
}
