using Microsoft.Data.Sqlite;

namespace Fraylo.Infrastructure.Persistence;

public static class DatabasePathHelper
{
    public const string DefaultDatabaseFileName = "fraylo.db";
    public const string DefaultAppFolderName = "Fraylo";

    public static string GetDefaultDatabaseDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, DefaultAppFolderName);
    }

    public static string GetDefaultDatabasePath()
    {
        return Path.Combine(GetDefaultDatabaseDirectory(), DefaultDatabaseFileName);
    }

    public static string BuildConnectionString(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 5
        };

        return builder.ToString();
    }
}
