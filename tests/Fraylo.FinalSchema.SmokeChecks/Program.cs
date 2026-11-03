using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Infrastructure.Persistence;

var root = Path.Combine(Path.GetTempPath(), "Fraylo_final_schema_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    await VerifySavedSqlMatchesEfModelAsync();

    var bootstrapper = new SqliteDatabaseBootstrapper();
    var freshPath = Path.Combine(root, "fresh.db");
    var fresh = await bootstrapper.BootstrapAsync(freshPath);
    Assert(fresh.IsBootstrapSuccessful && fresh.SchemaVersion == 6 && !fresh.DatabaseReset,
        "A fresh database must initialize directly from schema version 6.");
    await VerifyFinalSchemaAsync(freshPath);

    var legacyPath = Path.Combine(root, "legacy.db");
    await CreateLegacyFixtureAsync(legacyPath);
    var reset = await bootstrapper.BootstrapAsync(legacyPath);
    Assert(reset.IsBootstrapSuccessful && reset.SchemaVersion == 6 && reset.DatabaseReset,
        "A legacy database must be backed up and reset to schema version 6.");
    await VerifyFinalSchemaAsync(legacyPath);
    await VerifyBackupAsync(root, "legacy.db", "LegacyTable", "legacy-test-row");

    var previousPath = Path.Combine(root, "previous.db");
    await CreateVersion5FixtureAsync(previousPath);
    var previous = await bootstrapper.BootstrapAsync(previousPath);
    Assert(previous.IsBootstrapSuccessful && previous.SchemaVersion == 6 && previous.DatabaseReset,
        "A version 5 database must be backed up and reset to schema version 6 (no upgrade chain).");
    await VerifyFinalSchemaAsync(previousPath);
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(previousPath)))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Project;";
        Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 0, "Reset database must start with empty data.");
    }
    await VerifyBackupAsync(root, "previous.db", "Project", "old-project");

    Console.WriteLine("Final SQL/model parity, fresh bootstrap, legacy reset, version 5 reset, and backups: PASS");
    return 0;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

static async Task VerifySavedSqlMatchesEfModelAsync()
{
    var repositoryRoot = Directory.GetCurrentDirectory();
    var sqlPath = Path.Combine(repositoryRoot, "src", "Fraylo.Infrastructure", "Persistence", "Sql", "final-schema.sql");
    var savedSql = await File.ReadAllTextAsync(sqlPath);
    var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite("Data Source=:memory:").Options;
    await using var db = new FrayloDbContext(options);
    var generatedSql = db.Database.GenerateCreateScript() + Environment.NewLine + "PRAGMA user_version = 6;" + Environment.NewLine;
    var saved = NormalizeSql(savedSql).Split('\n');
    var generated = NormalizeSql(generatedSql).Split('\n');
    var difference = Enumerable.Range(0, Math.Min(saved.Length, generated.Length)).FirstOrDefault(i => saved[i] != generated[i], -1);
    Assert(difference < 0 && saved.Length == generated.Length,
        difference < 0 ? "Saved SQL has a different number of lines." : $"SQL mismatch at line {difference + 1}: saved '{saved[difference]}', generated '{generated[difference]}'.");
}

static async Task VerifyFinalSchemaAsync(string path)
{
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await connection.OpenAsync();

    await using (var versionCommand = connection.CreateCommand())
    {
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert(Convert.ToInt32(await versionCommand.ExecuteScalarAsync()) == 6, "Database schema version must equal 6.");
    }

    await using (var legacyTablesCommand = connection.CreateCommand())
    {
        legacyTablesCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = '__EFMigrationsHistory';";
        Assert(Convert.ToInt32(await legacyTablesCommand.ExecuteScalarAsync()) == 0, "EF migration history must be absent.");
    }

    foreach (var table in new[] { "Project", "Scene", "Asset", "Job", "ProviderAccount", "VideoRevision", "PublishRecord", "ProjectGroup" })
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        var found = false;
        while (await reader.ReadAsync())
        {
            found = true;
        }
        Assert(found, $"Required table {table} must exist.");
    }
}

// Schema 5 là bản trước khi gộp; có dữ liệu để kiểm tra việc sao lưu trước khi tạo lại.
static async Task CreateVersion5FixtureAsync(string path)
{
    var sql = (await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "src", "Fraylo.Infrastructure", "Persistence", "Sql", "final-schema.sql")))
        .Replace("PRAGMA user_version = 6;", "PRAGMA user_version = 5;", StringComparison.Ordinal);
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql + "\nINSERT INTO Project (id,title,brief,status,created_at_utc,updated_at_utc,revision) VALUES ('old-project','Dự án cũ','','Draft','now','now',1);";
    await command.ExecuteNonQueryAsync();
}

static async Task CreateLegacyFixtureAsync(string path)
{
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE LegacyTable (id TEXT PRIMARY KEY); INSERT INTO LegacyTable(id) VALUES ('legacy-test-row');";
    await command.ExecuteNonQueryAsync();
}

static async Task VerifyBackupAsync(string root, string databaseName, string table, string expectedId)
{
    var backups = Directory.GetFiles(root, $"{databaseName}.pre-reset-*.bak");
    Assert(backups.Length == 1, $"Exactly one pre-reset backup must be created for {databaseName}.");
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(backups[0]));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT id FROM \"{table}\";";
    Assert((string?)await command.ExecuteScalarAsync() == expectedId, "Pre-reset backup must preserve legacy data.");
}

static string NormalizeSql(string sql)
    => string.Join('\n', sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.TrimEnd())).Trim();

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
