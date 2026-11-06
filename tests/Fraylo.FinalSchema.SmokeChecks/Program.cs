using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Infrastructure.Persistence;

if (args.Length == 2 && args[0] == "--emit-final-schema")
{
    var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite("Data Source=:memory:").Options;
    await using var db = new FrayloDbContext(options);
    await File.WriteAllTextAsync(args[1], db.Database.GenerateCreateScript() + Environment.NewLine + "PRAGMA user_version = 9;" + Environment.NewLine);
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "Fraylo_final_schema_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    await VerifySavedSqlMatchesEfModelAsync();

    var bootstrapper = new SqliteDatabaseBootstrapper();
    var freshPath = Path.Combine(root, "fresh.db");
    var fresh = await bootstrapper.BootstrapAsync(freshPath);
    Assert(fresh.IsBootstrapSuccessful && fresh.SchemaVersion == 9 && !fresh.DatabaseReset,
        "A fresh database must initialize directly from schema version 9.");
    await VerifyFinalSchemaAsync(freshPath);

    var legacyPath = Path.Combine(root, "legacy.db");
    await CreateLegacyFixtureAsync(legacyPath);
    var reset = await bootstrapper.BootstrapAsync(legacyPath);
    Assert(reset.IsBootstrapSuccessful && reset.SchemaVersion == 9 && reset.DatabaseReset,
        "A legacy database must be backed up and reset to schema version 9.");
    await VerifyFinalSchemaAsync(legacyPath);
    await VerifyBackupAsync(root, "legacy.db", "LegacyTable", "legacy-test-row");

    var previousPath = Path.Combine(root, "previous.db");
    await CreateVersion6FixtureAsync(previousPath);
    var previous = await bootstrapper.BootstrapAsync(previousPath);
    Assert(previous.IsBootstrapSuccessful && previous.SchemaVersion == 9 && previous.DatabaseReset,
        "A version 6 database must be backed up and reset to schema version 9 (no upgrade chain).");
    await VerifyFinalSchemaAsync(previousPath);
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(previousPath)))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Project;";
        Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 0, "Reset database must start with empty data.");
    }
    await VerifyBackupAsync(root, "previous.db", "Project", "old-project");

    await VerifyFacebookUpgradeAsync(root, bootstrapper, freshPath);
    await VerifyFacebookRollbackAsync(root, bootstrapper);
    await VerifyScheduleUpgradeAsync(root, bootstrapper, freshPath);
    await VerifyScheduleRollbackAsync(root, bootstrapper);
    await VerifyFutureVersionAsync(root, bootstrapper);

    Console.WriteLine("Facebook v7 upgrade preservation/parity/backup/rollback/future-version: PASS");
    Console.WriteLine("Facebook v8 -> v9 (schedule/draft tables) preservation/parity/backup/rollback: PASS");
    Console.WriteLine("Final SQL/model parity, fresh bootstrap, legacy reset, version 6 reset, and backups: PASS");
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
    var generatedSql = db.Database.GenerateCreateScript() + Environment.NewLine + "PRAGMA user_version = 9;" + Environment.NewLine;
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
        Assert(Convert.ToInt32(await versionCommand.ExecuteScalarAsync()) == 9, "Database schema version must equal 9.");
    }

    await using (var legacyTablesCommand = connection.CreateCommand())
    {
        legacyTablesCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = '__EFMigrationsHistory';";
        Assert(Convert.ToInt32(await legacyTablesCommand.ExecuteScalarAsync()) == 0, "EF migration history must be absent.");
    }

    foreach (var table in new[] { "Project", "Scene", "Asset", "Job", "ProviderAccount", "VideoRevision", "PublishRecord", "FacebookTarget", "FacebookPostBatch", "FacebookPostTask", "FacebookPostTaskMedia", "FacebookTaskSchedule", "FacebookDraft" })
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

// Schema 6 là bản trước khi bỏ Nhóm dự án; có dữ liệu để kiểm tra việc sao lưu trước khi tạo lại.
static async Task CreateVersion6FixtureAsync(string path)
{
    var sql = (await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "src", "Fraylo.Infrastructure", "Persistence", "Sql", "final-schema.sql")))
        .Replace("PRAGMA user_version = 9;", "PRAGMA user_version = 6;", StringComparison.Ordinal);
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

// Dựng lại schema cũ bằng cách bỏ các câu lệnh của các bước nâng cấp khỏi final-schema.sql hiện hành.
static async Task<string> VersionSqlAsync(int version)
{
    var sqlDirectory = Path.Combine(Directory.GetCurrentDirectory(), "src", "Fraylo.Infrastructure", "Persistence", "Sql");
    var sql = await File.ReadAllTextAsync(Path.Combine(sqlDirectory, "final-schema.sql"));
    foreach (var migrationName in version == 7 ? new[] { "facebook-v8.sql", "facebook-v9.sql" } : new[] { "facebook-v9.sql" })
    {
        var migration = await File.ReadAllTextAsync(Path.Combine(sqlDirectory, migrationName));
        foreach (var statement in migration.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(s => !string.IsNullOrWhiteSpace(s))) sql = sql.Replace(statement.Trim() + ";", "", StringComparison.Ordinal);
    }
    return sql.Replace("PRAGMA user_version = 9;", $"PRAGMA user_version = {version};", StringComparison.Ordinal);
}

static Task SeedVersion7Async(string path) => SeedVersionAsync(path, 7);

static async Task SeedVersionAsync(string path, int version)
{
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = await VersionSqlAsync(version) + """
        INSERT INTO Project (id,title,brief,status,created_at_utc,updated_at_utc,revision) VALUES ('kept-project','Giữ nguyên dự án','brief','Draft','now','now',1);
        INSERT INTO BrowserProfile (id,kind,account_identity,visibility_state,session_status,operational_state,provider_key,display_name,lifecycle,revision,created_at_utc,updated_at_utc)
        VALUES ('kept-profile','Facebook','Facebook (123)','Hidden','Connected','Stopped','facebook','Tài khoản giữ nguyên','Active',4,'now','now');
        INSERT INTO ProviderAccount (id,provider_key,profile_id,display_name,verified_identity,enabled,lifecycle,revision,created_at_utc,updated_at_utc)
        VALUES ('kept-account','facebook','kept-profile','Giữ tài khoản','Facebook (123)',1,'Active',5,'now','now');
        INSERT INTO AppSettings (key,value,updated_at_utc) VALUES ('keep-setting','keep-value','now');
        """;
    await command.ExecuteNonQueryAsync();
}

static async Task<Dictionary<string, string>> ReadSchemaObjectsAsync(string path)
{
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY name;";
    await using var reader = await command.ExecuteReaderAsync();
    var result = new Dictionary<string, string>();
    while (await reader.ReadAsync()) result.Add(reader.GetString(0), NormalizeSql(reader.GetString(1)));
    return result;
}

static async Task VerifyFacebookUpgradeAsync(string root, SqliteDatabaseBootstrapper bootstrapper, string freshPath)
{
    var path = Path.Combine(root, "v7.db");
    await SeedVersion7Async(path);
    var result = await bootstrapper.BootstrapAsync(path);
    Assert(result.SchemaVersion == 9 && !result.DatabaseReset, "Facebook upgrade must never reset version 7 data.");
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM Project WHERE id='kept-project';";
        Assert((string?)await command.ExecuteScalarAsync() == "Giữ nguyên dự án", "Project data must survive Facebook upgrade.");
        command.CommandText = "SELECT account_identity || ':' || session_status || ':' || revision FROM BrowserProfile WHERE id='kept-profile';";
        Assert((string?)await command.ExecuteScalarAsync() == "Facebook (123):Connected:4", "Account identity/session/revision must survive Facebook upgrade.");
        command.CommandText = "SELECT verified_identity || ':' || enabled || ':' || revision FROM ProviderAccount WHERE id='kept-account';";
        Assert((string?)await command.ExecuteScalarAsync() == "Facebook (123):1:5", "Provider account identity/enabled/revision must survive Facebook upgrade.");
        command.CommandText = "SELECT value FROM AppSettings WHERE key='keep-setting';";
        Assert((string?)await command.ExecuteScalarAsync() == "keep-value", "Settings must survive Facebook upgrade.");
    }
    var upgraded = await ReadSchemaObjectsAsync(path);
    var fresh = await ReadSchemaObjectsAsync(freshPath);
    Assert(upgraded.Count == fresh.Count && fresh.All(p => upgraded.TryGetValue(p.Key, out var value) && value == p.Value), "Upgraded and fresh schemas must have identical objects.");
    var backups = Directory.GetFiles(root, "v7.db.pre-facebook-*.bak");
    Assert(backups.Length == 1, "Exactly one backup must precede version 7 upgrade.");
    await using var backup = new SqliteConnection(DatabasePathHelper.BuildConnectionString(backups[0]));
    await backup.OpenAsync();
    await using var check = backup.CreateCommand();
    check.CommandText = "PRAGMA user_version;";
    Assert(Convert.ToInt32(await check.ExecuteScalarAsync()) == 7, "Backup must preserve original version 7.");
    check.CommandText = "SELECT title FROM Project WHERE id='kept-project';";
    Assert((string?)await check.ExecuteScalarAsync() == "Giữ nguyên dự án", "Backup must contain pre-upgrade project data.");
    var second = await bootstrapper.BootstrapAsync(path);
    Assert(!second.DatabaseReset && Directory.GetFiles(root, "v7.db.pre-facebook-*.bak").Length == 1, "Opening version 9 again must not upgrade or backup twice.");
}

static async Task VerifyFacebookRollbackAsync(string root, SqliteDatabaseBootstrapper bootstrapper)
{
    var path = Path.Combine(root, "broken-v7.db");
    await SeedVersion7Async(path);
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE FacebookPostTask(id TEXT PRIMARY KEY); INSERT INTO FacebookPostTask VALUES('conflict');";
        await command.ExecuteNonQueryAsync();
    }
    var original = await ReadSchemaObjectsAsync(path);
    var failed = false;
    try { await bootstrapper.BootstrapAsync(path); } catch (SqliteException) { failed = true; }
    Assert(failed, "Conflicting upgrade must fail.");
    var after = await ReadSchemaObjectsAsync(path);
    Assert(original.Count == after.Count && original.All(p => after.TryGetValue(p.Key, out var value) && value == p.Value), "Failed upgrade must roll back every CREATE before the failure.");
    await using var check = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await check.OpenAsync();
    await using var query = check.CreateCommand();
    query.CommandText = "PRAGMA user_version;";
    Assert(Convert.ToInt32(await query.ExecuteScalarAsync()) == 7, "Failed upgrade must remain version 7.");
    query.CommandText = "SELECT title FROM Project WHERE id='kept-project';";
    Assert((string?)await query.ExecuteScalarAsync() == "Giữ nguyên dự án", "Failed upgrade must preserve existing data.");
}

// Dữ liệu Facebook đang có (nhóm, lô, tác vụ, ảnh) của bản 8 phải còn nguyên sau khi cộng bảng hẹn giờ/nháp.
static async Task SeedVersion8WithTaskAsync(string path)
{
    await SeedVersionAsync(path, 8);
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO FacebookTarget (id,profile_id,name,url,lifecycle,created_at_utc,updated_at_utc) VALUES ('kept-target','kept-profile','Nhóm giữ nguyên','https://www.facebook.com/groups/kept/','Active','now','now');
        INSERT INTO FacebookPostBatch (id,profile_id,request_hash,created_at_utc) VALUES ('kept-batch','kept-profile','hash','now');
        INSERT INTO FacebookPostTask (id,batch_id,target_id,profile_id,account_identity_snapshot,group_name_snapshot,group_url_snapshot,text_snapshot,state,message,cancel_requested,revision,created_at_utc,updated_at_utc)
        VALUES ('kept-task','kept-batch','kept-target','kept-profile','Facebook (123)','Nhóm giữ nguyên','https://www.facebook.com/groups/kept/','Bài giữ nguyên','Published','ok',0,3,'now','now');
        """;
    await command.ExecuteNonQueryAsync();
}

static async Task VerifyScheduleUpgradeAsync(string root, SqliteDatabaseBootstrapper bootstrapper, string freshPath)
{
    var path = Path.Combine(root, "v8.db");
    await SeedVersion8WithTaskAsync(path);
    var result = await bootstrapper.BootstrapAsync(path);
    Assert(result.SchemaVersion == 9 && !result.DatabaseReset, "Upgrading version 8 must never reset data.");
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text_snapshot || ':' || state || ':' || revision FROM FacebookPostTask WHERE id='kept-task';";
        Assert((string?)await command.ExecuteScalarAsync() == "Bài giữ nguyên:Published:3", "Facebook tasks must survive the version 9 upgrade.");
        command.CommandText = "SELECT title FROM Project WHERE id='kept-project';";
        Assert((string?)await command.ExecuteScalarAsync() == "Giữ nguyên dự án", "Project data must survive the version 9 upgrade.");
        command.CommandText = "PRAGMA user_version;";
        Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 9, "Upgraded database must be version 9.");
        command.CommandText = "INSERT INTO FacebookTaskSchedule (task_id,due_at_utc) VALUES ('kept-task','2030-01-01T00:00:00.0000000+00:00');";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "DELETE FROM FacebookPostTask WHERE id='kept-task'; SELECT COUNT(*) FROM FacebookTaskSchedule;";
        Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 0, "Schedule rows must cascade with their task.");
    }
    var upgraded = await ReadSchemaObjectsAsync(path);
    var fresh = await ReadSchemaObjectsAsync(freshPath);
    Assert(upgraded.Count == fresh.Count && fresh.All(p => upgraded.TryGetValue(p.Key, out var value) && value == p.Value), "Version 8 upgraded and fresh schemas must have identical objects.");
    var backups = Directory.GetFiles(root, "v8.db.pre-facebook-v9-*.bak");
    Assert(backups.Length == 1, "Exactly one backup must precede the version 9 upgrade.");
    await using var backup = new SqliteConnection(DatabasePathHelper.BuildConnectionString(backups[0]));
    await backup.OpenAsync();
    await using var check = backup.CreateCommand();
    check.CommandText = "PRAGMA user_version;";
    Assert(Convert.ToInt32(await check.ExecuteScalarAsync()) == 8, "Backup must preserve original version 8.");
    await bootstrapper.BootstrapAsync(path);
    Assert(Directory.GetFiles(root, "v8.db.pre-facebook-v9-*.bak").Length == 1, "Opening version 9 again must not back up twice.");
}

static async Task VerifyScheduleRollbackAsync(string root, SqliteDatabaseBootstrapper bootstrapper)
{
    var path = Path.Combine(root, "broken-v8.db");
    await SeedVersion8WithTaskAsync(path);
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE FacebookDraft(id TEXT PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
    }
    var original = await ReadSchemaObjectsAsync(path);
    var failed = false;
    try { await bootstrapper.BootstrapAsync(path); } catch (SqliteException) { failed = true; }
    Assert(failed, "Conflicting version 9 upgrade must fail.");
    var after = await ReadSchemaObjectsAsync(path);
    Assert(original.Count == after.Count && original.All(p => after.TryGetValue(p.Key, out var value) && value == p.Value), "Failed version 9 upgrade must roll back every CREATE.");
    await using var check = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await check.OpenAsync();
    await using var query = check.CreateCommand();
    query.CommandText = "PRAGMA user_version;";
    Assert(Convert.ToInt32(await query.ExecuteScalarAsync()) == 8, "Failed version 9 upgrade must remain version 8.");
    query.CommandText = "SELECT state FROM FacebookPostTask WHERE id='kept-task';";
    Assert((string?)await query.ExecuteScalarAsync() == "Published", "Failed version 9 upgrade must preserve Facebook tasks.");
}

static async Task VerifyFutureVersionAsync(string root, SqliteDatabaseBootstrapper bootstrapper)
{
    var path = Path.Combine(root, "future.db");
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version=10; CREATE TABLE FutureData(id TEXT); INSERT INTO FutureData VALUES('kept');";
    await command.ExecuteNonQueryAsync();
    var failed = false;
    try { await bootstrapper.BootstrapAsync(path); } catch (InvalidOperationException) { failed = true; }
    Assert(failed, "Schema version 10 must be rejected.");
    command.CommandText = "SELECT id FROM FutureData;";
    Assert((string?)await command.ExecuteScalarAsync() == "kept", "Rejected future schema must preserve data.");
}
