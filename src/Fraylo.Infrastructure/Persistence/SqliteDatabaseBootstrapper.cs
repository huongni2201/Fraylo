using System.Reflection;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Fraylo.Infrastructure.Persistence.Interceptors;

namespace Fraylo.Infrastructure.Persistence;

public sealed class SqliteDatabaseBootstrapper : ISqliteDatabaseBootstrapper
{
    private const int CurrentSchemaVersion = 8;
    private const string SchemaResourceName = "Fraylo.Infrastructure.Persistence.Sql.final-schema.sql";
    private readonly ILogger<SqliteDatabaseBootstrapper>? _logger;

    public SqliteDatabaseBootstrapper(ILogger<SqliteDatabaseBootstrapper>? logger = null)
    {
        _logger = logger;
    }

    public async Task<DatabaseBootstrapResult> BootstrapAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default)
    {
        var dbPath = databasePath ?? DatabasePathHelper.GetDefaultDatabasePath();
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var connectionString = DatabasePathHelper.BuildConnectionString(dbPath);
        string initialJournalMode;
        bool databaseReset = false;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            initialJournalMode = await ExecuteScalarTextAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

            var schemaVersion = Convert.ToInt32(await ExecuteScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false));
            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"Database schema version {schemaVersion} is not supported; expected {CurrentSchemaVersion}.");
            }

            if (schemaVersion == 7)
            {
                var backupPath = CreateSchemaBackup(dbPath, connectionString, "pre-facebook", cancellationToken);
                _logger?.LogInformation("Created pre-Facebook SQLite backup at {BackupPath}", backupPath);
                await UpgradeFacebookSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            else if (schemaVersion < CurrentSchemaVersion)
            {
                // Không còn chuỗi nâng cấp: schema cũ được sao lưu rồi tạo lại từ final-schema.sql (dữ liệu rỗng).
                var tables = await ListUserTablesAsync(connection, cancellationToken).ConfigureAwait(false);
                if (tables.Count > 0)
                {
                    var backupPath = CreateSchemaBackup(dbPath, connectionString, "pre-reset", cancellationToken);
                    _logger?.LogInformation("Created pre-reset SQLite backup at {BackupPath}", backupPath);
                    await DropUserTablesAsync(connection, tables, cancellationToken).ConfigureAwait(false);
                    databaseReset = true;
                }

                await ExecuteFinalSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            await VerifyFinalSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        string sqliteVersion;
        string journalMode;
        bool foreignKeysEnabled;
        int busyTimeout;
        int synchronousMode;
        var verifyOptions = new DbContextOptionsBuilder<FrayloDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;

        await using (var db = new FrayloDbContext(verifyOptions))
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = db.Database.GetDbConnection();
            sqliteVersion = await ExecuteScalarTextAsync(connection, "SELECT sqlite_version();", cancellationToken).ConfigureAwait(false);
            journalMode = await ExecuteScalarTextAsync(connection, "PRAGMA journal_mode;", cancellationToken).ConfigureAwait(false);
            foreignKeysEnabled = Convert.ToInt32(await ExecuteScalarAsync(connection, "PRAGMA foreign_keys;", cancellationToken).ConfigureAwait(false)) == 1;
            busyTimeout = Convert.ToInt32(await ExecuteScalarAsync(connection, "PRAGMA busy_timeout;", cancellationToken).ConfigureAwait(false));
            synchronousMode = Convert.ToInt32(await ExecuteScalarAsync(connection, "PRAGMA synchronous;", cancellationToken).ConfigureAwait(false));
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        _logger?.LogInformation(
            "SQLite bootstrap completed for {Path}: schema={SchemaVersion}, SQLite {Version}, WAL={JournalMode}, FK={FK}, BusyTimeout={Timeout}ms, Synchronous={Sync}",
            dbPath, CurrentSchemaVersion, sqliteVersion, journalMode, foreignKeysEnabled, busyTimeout, synchronousMode);

        Console.WriteLine($"[Fraylo SQLite Bootstrap] Database: {dbPath}");
        Console.WriteLine($"[Fraylo SQLite Bootstrap] Schema Version: {CurrentSchemaVersion}");
        Console.WriteLine($"[Fraylo SQLite Bootstrap] SQLite Version: {sqliteVersion}");
        Console.WriteLine($"[Fraylo SQLite Bootstrap] Journal Mode: {journalMode}");
        Console.WriteLine($"[Fraylo SQLite Bootstrap] Foreign Keys: {(foreignKeysEnabled ? "ON" : "OFF")}");
        Console.WriteLine($"[Fraylo SQLite Bootstrap] Busy Timeout: {busyTimeout}ms");
        Console.WriteLine($"[Fraylo SQLite Bootstrap] Synchronous: {synchronousMode}");

        var isSuccess = string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase)
                        && foreignKeysEnabled
                        && busyTimeout == 5000
                        && synchronousMode == 1;

        return new DatabaseBootstrapResult(
            DatabasePath: dbPath,
            SqliteVersion: sqliteVersion,
            JournalMode: journalMode,
            ForeignKeysEnabled: foreignKeysEnabled,
            BusyTimeout: busyTimeout,
            SynchronousMode: synchronousMode,
            SchemaVersion: CurrentSchemaVersion,
            DatabaseReset: databaseReset,
            IsBootstrapSuccessful: isSuccess);
    }

    private static async Task UpgradeFacebookSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Fraylo.Infrastructure.Persistence.Sql.facebook-v8.sql")
            ?? throw new InvalidOperationException("Facebook schema resource was not found.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql + "\nPRAGMA user_version = 8;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteFinalSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"Embedded final schema '{SchemaResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<string>> ListUserTablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task DropUserTablesAsync(SqliteConnection connection, IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = OFF;", cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            var quotedName = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"DROP TABLE {quotedName};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyFinalSchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var tables = await ListUserTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        if (tables.Contains("Workspace", StringComparer.OrdinalIgnoreCase)
            || tables.Contains("Account", StringComparer.OrdinalIgnoreCase)
            || tables.Contains("__EFMigrationsHistory", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Final database schema must not contain legacy Workspace/Account tables or EF migration history.");
        }

        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "workspace_id", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Final database table '{table}' still contains legacy workspace_id.");
                }
            }
        }
    }

    private static string CreateSchemaBackup(string databasePath, string connectionString, string label, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var suffix = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N");
        var backupPath = $"{databasePath}.{label}-{suffix}.bak";
        using var source = new SqliteConnection(connectionString);
        using var destination = new SqliteConnection(DatabasePathHelper.BuildConnectionString(backupPath));
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        return backupPath;
    }

    private static async Task<object?> ExecuteScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ExecuteScalarTextAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
        => (await ExecuteScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false))?.ToString() ?? string.Empty;

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
