using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Infrastructure.Persistence;

if (args.Length == 2 && args[0] == "--emit-final-schema")
{
    var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite("Data Source=:memory:").Options;
    await using var db = new FrayloDbContext(options);
    await File.WriteAllTextAsync(args[1], db.Database.GenerateCreateScript() + Environment.NewLine + "PRAGMA user_version = 15;" + Environment.NewLine);
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
    Assert(fresh.IsBootstrapSuccessful && fresh.SchemaVersion == 15 && !fresh.DatabaseReset,
        "A fresh database must initialize directly from schema version 15.");
    await VerifyFinalSchemaAsync(freshPath);

    var legacyPath = Path.Combine(root, "legacy.db");
    await CreateLegacyFixtureAsync(legacyPath);
    var reset = await bootstrapper.BootstrapAsync(legacyPath);
    Assert(reset.IsBootstrapSuccessful && reset.SchemaVersion == 15 && reset.DatabaseReset,
        "A legacy database must be backed up and reset to schema version 15.");
    await VerifyFinalSchemaAsync(legacyPath);
    await VerifyBackupAsync(root, "legacy.db", "LegacyTable", "legacy-test-row");

    foreach (var release in new[] { "v1.0.2", "v1.0.3" })
    {
        var path = Path.Combine(root, release + ".db");
        await using (var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = await File.ReadAllTextAsync($"tests/Fraylo.FinalSchema.SmokeChecks/Fixtures/{release}.sql") +
                "INSERT INTO Project(id,title,brief,status,created_at_utc,updated_at_utc,revision) VALUES('kept','Old project','','Draft','now','now',1);" +
                "INSERT INTO Asset(id,project_id,storage_root_id,relative_path,asset_type,byte_size,sha256_hash,created_at_utc) VALUES('asset','kept','root','images/kept.png','Image',123,'hash','now');";
            await cmd.ExecuteNonQueryAsync();
        }
        var result = await bootstrapper.BootstrapAsync(path);
        Assert(!result.DatabaseReset && result.SchemaVersion == 15, "Released v6 database must upgrade without reset.");
        await bootstrapper.BootstrapAsync(path);
        await using var verify = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
        await verify.OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT title FROM Project WHERE id='kept';";
        Assert((string?)await query.ExecuteScalarAsync() == "Old project", "Released project must survive upgrade and restart.");
        query.CommandText = "SELECT relative_path || ':' || project_id FROM Asset WHERE id='asset';";
        Assert((string?)await query.ExecuteScalarAsync() == "images/kept.png:kept", "Media metadata and project relationship must survive.");
        query.CommandText = "PRAGMA foreign_key_check;";
        Assert(await query.ExecuteScalarAsync() is null, "All relationships must remain valid.");
        Console.WriteLine(release + " -> v15 preservation/restart PASS");
    }

    var brokenRelease = Path.Combine(root, "broken-release.db");
    await using (var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(brokenRelease)))
    {
        await db.OpenAsync(); await using var cmd = db.CreateCommand();
        cmd.CommandText = await File.ReadAllTextAsync("tests/Fraylo.FinalSchema.SmokeChecks/Fixtures/v1.0.3.sql") +
            "CREATE TABLE AnalyticsAnalysisReport(id TEXT);";
        await cmd.ExecuteNonQueryAsync();
    }
    var originalSchema = await ReadSchemaObjectsAsync(brokenRelease);
    var failed = false;
    try { await bootstrapper.BootstrapAsync(brokenRelease); } catch (SqliteException) { failed = true; }
    var restoredSchema = await ReadSchemaObjectsAsync(brokenRelease);
    Assert(failed && originalSchema.Count == restoredSchema.Count && originalSchema.All(p => restoredSchema.GetValueOrDefault(p.Key) == p.Value),
        "A late migration failure must roll back every v6 upgrade step.");
    Console.WriteLine("Released v6 full-chain rollback PASS");

    await VerifyFacebookUpgradeAsync(root, bootstrapper, freshPath);
    await VerifyFacebookRollbackAsync(root, bootstrapper);
    await VerifyScheduleUpgradeAsync(root, bootstrapper, freshPath);
    await VerifyScheduleRollbackAsync(root, bootstrapper);
    await VerifyPostsUpgradeAsync(root, bootstrapper, freshPath);
    await VerifyAnalyticsUpgradeAsync(root, bootstrapper, freshPath);
    await VerifyAnalysisJobUpgradeAsync(root, bootstrapper, freshPath);
    await VerifyRewriteUpgradeAsync(root, bootstrapper, freshPath);
    await VerifyContentModeUpgradeAsync(root, bootstrapper, freshPath);
    await VerifyFacebookActivityUpgradeAsync(root, bootstrapper);
    await VerifyFutureVersionAsync(root, bootstrapper);

    Console.WriteLine("Facebook v7 upgrade preservation/parity/backup/rollback/future-version: PASS");
    Console.WriteLine("Facebook v8 -> v9 (schedule/draft tables) preservation/parity/backup/rollback: PASS");
    Console.WriteLine("Final SQL/model parity, fresh bootstrap, legacy reset, released v6 preservation, and backups: PASS");
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
    var generatedSql = db.Database.GenerateCreateScript() + Environment.NewLine + "PRAGMA user_version = 15;" + Environment.NewLine;
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
        Assert(Convert.ToInt32(await versionCommand.ExecuteScalarAsync()) == 15, "Database schema version must equal 15.");
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
    var sql = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "tests", "Fraylo.FinalSchema.SmokeChecks", "Fixtures", "schema-v9.sql"));
    foreach (var migrationName in version == 7 ? new[] { "facebook-v8.sql", "facebook-v9.sql" } : new[] { "facebook-v9.sql" })
    {
        var migration = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "tests", "Fraylo.FinalSchema.SmokeChecks", "Fixtures", migrationName));
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
    while (await reader.ReadAsync()) result.Add(reader.GetString(0), CanonicalSchema(reader.GetString(1)));
    return result;
}

static async Task VerifyFacebookUpgradeAsync(string root, SqliteDatabaseBootstrapper bootstrapper, string freshPath)
{
    var path = Path.Combine(root, "v7.db");
    await SeedVersion7Async(path);
    var result = await bootstrapper.BootstrapAsync(path);
    Assert(result.SchemaVersion == 15 && !result.DatabaseReset, "Facebook upgrade must never reset version 7 data.");
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
    foreach (var item in fresh.Where(p => !upgraded.TryGetValue(p.Key, out var value) || value != p.Value))
        Console.WriteLine($"SCHEMA DIFF {item.Key}\nFresh: {item.Value}\nUpgrade: {upgraded.GetValueOrDefault(item.Key)}");
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
    Assert(!second.DatabaseReset && Directory.GetFiles(root, "v7.db.pre-facebook-*.bak").Length == 1, "Opening version 15 again must not upgrade or backup twice.");
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
    Assert(result.SchemaVersion == 15 && !result.DatabaseReset, "Upgrading version 8 must never reset data.");
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text_snapshot || ':' || state || ':' || revision FROM FacebookPostTask WHERE id='kept-task';";
        Assert((string?)await command.ExecuteScalarAsync() == "Bài giữ nguyên:Published:3", "Facebook tasks must survive the version 10 upgrade.");
        command.CommandText = "SELECT title FROM Project WHERE id='kept-project';";
        Assert((string?)await command.ExecuteScalarAsync() == "Giữ nguyên dự án", "Project data must survive the version 10 upgrade.");
        command.CommandText = "PRAGMA user_version;";
        Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 15, "Upgraded database must be version 15.");
        command.CommandText = "INSERT INTO FacebookTaskSchedule (task_id,due_at_utc) VALUES ('kept-task','2030-01-01T00:00:00.0000000+00:00');";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "DELETE FROM FacebookPostTask WHERE id='kept-task'; SELECT COUNT(*) FROM FacebookTaskSchedule;";
        Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 0, "Schedule rows must cascade with their task.");
    }
    var upgraded = await ReadSchemaObjectsAsync(path);
    var fresh = await ReadSchemaObjectsAsync(freshPath);
    Assert(upgraded.Count == fresh.Count && fresh.All(p => upgraded.TryGetValue(p.Key, out var value) && value == p.Value), "Version 8 upgraded and fresh schemas must have identical objects.");
    var backups = Directory.GetFiles(root, "v8.db.pre-facebook-v9-*.bak");
    Assert(backups.Length == 1, "Exactly one backup must precede the version 10 upgrade.");
    await using var backup = new SqliteConnection(DatabasePathHelper.BuildConnectionString(backups[0]));
    await backup.OpenAsync();
    await using var check = backup.CreateCommand();
    check.CommandText = "PRAGMA user_version;";
    Assert(Convert.ToInt32(await check.ExecuteScalarAsync()) == 8, "Backup must preserve original version 8.");
    await bootstrapper.BootstrapAsync(path);
    Assert(Directory.GetFiles(root, "v8.db.pre-facebook-v9-*.bak").Length == 1, "Opening version 12 again must not back up twice.");
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
    Assert(failed, "Conflicting version 10 upgrade must fail.");
    var after = await ReadSchemaObjectsAsync(path);
    Assert(original.Count == after.Count && original.All(p => after.TryGetValue(p.Key, out var value) && value == p.Value), "Failed version 10 upgrade must roll back every CREATE.");
    await using var check = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await check.OpenAsync();
    await using var query = check.CreateCommand();
    query.CommandText = "PRAGMA user_version;";
    Assert(Convert.ToInt32(await query.ExecuteScalarAsync()) == 8, "Failed version 10 upgrade must remain version 8.");
    query.CommandText = "SELECT state FROM FacebookPostTask WHERE id='kept-task';";
    Assert((string?)await query.ExecuteScalarAsync() == "Published", "Failed version 10 upgrade must preserve Facebook tasks.");
}

static async Task VerifyFacebookActivityUpgradeAsync(string root, SqliteDatabaseBootstrapper bootstrapper)
{
    var path = Path.Combine(root, "v14-activity.db");
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = await File.ReadAllTextAsync("tests/Fraylo.FinalSchema.SmokeChecks/Fixtures/schema-v13.sql") + """
        ALTER TABLE "FacebookDraft" ADD "rewrite_content" INTEGER NOT NULL DEFAULT 1;
        PRAGMA user_version = 14;
        INSERT INTO BrowserProfile (id,kind,created_at_utc,updated_at_utc)
        VALUES ('fb-activity','Facebook','now','now');
        INSERT INTO FacebookTarget (id,profile_id,name,url,lifecycle,created_at_utc,updated_at_utc)
        VALUES ('kept-target','fb-activity','Nhóm','https://www.facebook.com/groups/kept/','Active','now','now');
        INSERT INTO FacebookPostBatch (id,profile_id,request_hash,created_at_utc)
        VALUES ('kept-batch','fb-activity','hash','now');
        INSERT INTO FacebookPostTask (id,batch_id,target_id,profile_id,account_identity_snapshot,group_name_snapshot,group_url_snapshot,text_snapshot,state,message,cancel_requested,revision,created_at_utc,updated_at_utc)
        VALUES ('kept-task','kept-batch','kept-target','fb-activity','Facebook (123)','Nhóm','https://www.facebook.com/groups/kept/','Bài cũ','Published','ok',0,3,'now','now');
        INSERT INTO FacebookAnalyticsPage (Id,ProfileId,FacebookPageId,Name,Url,VerifiedAtUtc)
        VALUES ('kept-page','fb-activity','123','Fanpage','https://www.facebook.com/example','now');
        INSERT INTO FacebookAnalyticsCapture (Id,PageId,Status,StartedAtUtc,Message,RecordCount,Source,Scope,Owner)
        VALUES ('kept-capture','kept-page','Partial','now','partial',2,'test','lifetime','test');
        """;
    await cmd.ExecuteNonQueryAsync();
    await connection.CloseAsync();
    var result = await bootstrapper.BootstrapAsync(path);
    Assert(result.SchemaVersion == 15 && !result.DatabaseReset, "v14 upgrades without resetting Facebook data.");
    await bootstrapper.BootstrapAsync(path);
    Assert(Directory.GetFiles(root, "v14-activity.db.pre-facebook-activity-v15-*.bak").Length == 1, "Activity log upgrade creates one backup.");
    await connection.OpenAsync();
    cmd.CommandText = "SELECT state || ':' || text_snapshot || ':' || activity_log_json FROM FacebookPostTask WHERE id='kept-task';";
    Assert((string?)await cmd.ExecuteScalarAsync() == "Published:Bài cũ:[]", "Legacy posting task keeps state and content.");
    cmd.CommandText = "SELECT Status || ':' || RecordCount || ':' || activity_log_json FROM FacebookAnalyticsCapture WHERE Id='kept-capture';";
    Assert((string?)await cmd.ExecuteScalarAsync() == "Partial:2:[]", "Legacy Analytics capture keeps status and count.");
    Console.WriteLine("v14 -> v15 Facebook tasks and Analytics preservation/backup/restart PASS");
}

static async Task VerifyContentModeUpgradeAsync(string root, SqliteDatabaseBootstrapper bootstrapper, string freshPath)
{
    var path = Path.Combine(root, "v13-content-mode.db");
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = await File.ReadAllTextAsync("tests/Fraylo.FinalSchema.SmokeChecks/Fixtures/schema-v13.sql") + """
            INSERT INTO FacebookDraft(id,name,profile_id,text,target_ids_json,image_asset_ids_json,revision,created_at_utc,updated_at_utc)
                VALUES('kept-draft','Nháp cũ','fb','Giữ nội dung','[]','[]',3,'now','now');
            """;
        await cmd.ExecuteNonQueryAsync();
    }
    var result = await bootstrapper.BootstrapAsync(path);
    Assert(result.SchemaVersion == 15 && !result.DatabaseReset, "v13 upgrades to content-mode without reset.");
    var fresh = await ReadSchemaObjectsAsync(freshPath);
    var upgraded = await ReadSchemaObjectsAsync(path);
    Assert(fresh.Count == upgraded.Count && fresh.All(p => upgraded.GetValueOrDefault(p.Key) == p.Value), "Content-mode migration matches fresh schema.");
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await connection.OpenAsync(); await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT text || ':' || revision || ':' || rewrite_content FROM FacebookDraft WHERE id='kept-draft';";
        Assert((string?)await cmd.ExecuteScalarAsync() == "Giữ nội dung:3:1", "Existing drafts retain text/revision and previous rewrite default.");
        cmd.CommandText = "UPDATE FacebookDraft SET rewrite_content=0 WHERE id='kept-draft';";
        await cmd.ExecuteNonQueryAsync();
    }
    await bootstrapper.BootstrapAsync(path);
    Assert(Directory.GetFiles(root, "v13-content-mode.db.pre-facebook-content-mode-v14-*.bak").Length == 1, "Content-mode upgrade is backed up once.");
    await using var check = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await check.OpenAsync(); await using var query = check.CreateCommand();
    query.CommandText = "SELECT rewrite_content FROM FacebookDraft WHERE id='kept-draft';";
    Assert(Convert.ToInt32(await query.ExecuteScalarAsync()) == 0, "Original choice persists across restart.");
    Console.WriteLine("v13 -> v15 draft preservation/parity/backup/restart PASS");
}

static async Task VerifyFutureVersionAsync(string root, SqliteDatabaseBootstrapper bootstrapper)
{
    var path = Path.Combine(root, "future.db");
    await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version=16; CREATE TABLE FutureData(id TEXT); INSERT INTO FutureData VALUES('kept');";
    await command.ExecuteNonQueryAsync();
    var failed = false;
    try { await bootstrapper.BootstrapAsync(path); } catch (InvalidOperationException) { failed = true; }
    Assert(failed, "Schema version 16 must be rejected.");
    command.CommandText = "SELECT id FROM FutureData;";
    Assert((string?)await command.ExecuteScalarAsync() == "kept", "Rejected future schema must preserve data.");
}


static async Task VerifyRewriteUpgradeAsync(string root, SqliteDatabaseBootstrapper bootstrapper, string freshPath)
{
    async Task Seed(string path, bool conflict = false)
    {
        await using var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = await File.ReadAllTextAsync("tests/Fraylo.FinalSchema.SmokeChecks/Fixtures/schema-v12.sql") + """
            INSERT INTO AnalyticsAnalysisReport(id,platform,account_id,source_id,created_at_utc,settings_revision) VALUES('report','tiktok','account','source','now',1);
            INSERT INTO Job(id,analytics_report_id,task_type,provider_key,storage_root_id,result_text,created_at_utc,updated_at_utc)
                VALUES('analysis','report','AnalyticsAnalysis','chatgpt-web','','preserved','now','now');
            INSERT INTO BrowserProfile(id,kind,created_at_utc,updated_at_utc) VALUES('profile','Facebook','now','now');
            INSERT INTO FacebookPostBatch(id,profile_id,request_hash,created_at_utc) VALUES('batch','profile','hash','now');
            """;
        if (conflict) cmd.CommandText += "CREATE TABLE Job_v13(id TEXT);";
        await cmd.ExecuteNonQueryAsync();
    }
    var path = Path.Combine(root, "v12-rewrite.db");
    await Seed(path);
    var result = await bootstrapper.BootstrapAsync(path);
    Assert(result.SchemaVersion == 15 && !result.DatabaseReset, "v12 -> v13 preserves data.");
    var fresh = await ReadSchemaObjectsAsync(freshPath);
    var upgraded = await ReadSchemaObjectsAsync(path);
    Assert(fresh.Count == upgraded.Count && fresh.All(p => upgraded.GetValueOrDefault(p.Key) == p.Value), "Rewrite migration matches fresh schema.");
    await using (var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await db.OpenAsync(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT analytics_report_id || ':' || result_text FROM Job WHERE id='analysis'";
        Assert((string?)await cmd.ExecuteScalarAsync() == "report:preserved", "Rewrite upgrade retains Analytics Job FK and result.");
        cmd.CommandText = "SELECT requires_rewrite FROM FacebookPostBatch WHERE id='batch'";
        Assert(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0, "Existing batches never enable rewrite retroactively.");
        cmd.CommandText = "INSERT INTO FacebookRewriteChunk(id,batch_id,ordinal,task_ids_json) VALUES('chunk','batch',0,'[]');";
        await cmd.ExecuteNonQueryAsync();
        foreach (var (project, report, chunk, type) in new[]
        {
            ("NULL", "NULL", "NULL", "FacebookContentRewrite"),
            ("NULL", "'report'", "'chunk'", "FacebookContentRewrite"),
            ("NULL", "NULL", "'chunk'", "Script"),
            ("NULL", "'report'", "'chunk'", "AnalyticsAnalysis")
        })
        {
            cmd.CommandText = $"INSERT INTO Job(id,project_id,analytics_report_id,facebook_rewrite_chunk_id,task_type,provider_key,storage_root_id,created_at_utc,updated_at_utc) VALUES('invalid',{project},{report},{chunk},'{type}','chatgpt-web','','now','now');";
            var failed = false; try { await cmd.ExecuteNonQueryAsync(); } catch (SqliteException) { failed = true; }
            Assert(failed, "Reject invalid Job scope: " + type);
        }
        cmd.CommandText = "PRAGMA foreign_key_check";
        Assert(await cmd.ExecuteScalarAsync() is null, "Rewrite upgrade FK integrity.");
    }
    Assert(Directory.GetFiles(root, "v12-rewrite.db.pre-facebook-rewrite-v13-*.bak").Length == 1, "Rewrite upgrade creates backup.");
    var broken = Path.Combine(root, "broken-v12-rewrite.db"); await Seed(broken, true);
    var before = await ReadSchemaObjectsAsync(broken);
    var rejected = false; try { await bootstrapper.BootstrapAsync(broken); } catch (SqliteException) { rejected = true; }
    var after = await ReadSchemaObjectsAsync(broken);
    Assert(rejected && before.Count == after.Count && before.All(p => after.GetValueOrDefault(p.Key) == p.Value), "v13 rollback restores every table/column on failure.");
    Console.WriteLine("Facebook rewrite v12 -> v13: preservation, scope constraints, backup and rollback PASS");
}

static async Task VerifyAnalysisJobUpgradeAsync(string root, SqliteDatabaseBootstrapper bootstrapper, string freshPath)
{
    var path = Path.Combine(root, "v11-analysis.db");
    async Task Seed(string target, bool conflict = false)
    {
        await using var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(target));
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = await File.ReadAllTextAsync("tests/Fraylo.FinalSchema.SmokeChecks/Fixtures/schema-v10.sql") +
            await File.ReadAllTextAsync("tests/Fraylo.FinalSchema.SmokeChecks/Fixtures/facebook-analytics-v11.sql") +
            """
            PRAGMA user_version=11;
            INSERT INTO Project(id,title,brief,status,created_at_utc,updated_at_utc) VALUES ('p','Project','','Draft','now','now');
            INSERT INTO Job(id,project_id,task_type,provider_key,storage_root_id,result_text,created_at_utc,updated_at_utc)
                VALUES ('j','p','Script','chatgpt-web','docs','kept','now','now');
            INSERT INTO BrowserProfile(id,kind,created_at_utc,updated_at_utc) VALUES ('profile','ChatGPT','now','now');
            INSERT INTO ProviderAccount(id,provider_key,profile_id,display_name,created_at_utc,updated_at_utc)
                VALUES ('account','chatgpt-web','profile','Fixture','now','now');
            INSERT INTO ExecutionBinding(job_id,provider_account_id,profile_id,capacity_config_revision,tab_slot_id,assigned_at_utc)
                VALUES ('j','account','profile',1,'slot','now');
            INSERT INTO ActiveSlotOccupancy(provider_account_id,tab_slot_id,job_id,lease_owner_id,lease_token,acquired_at_utc)
                VALUES ('account','slot','j','owner',1,'now');
            """;
        if (conflict) cmd.CommandText += "CREATE TABLE Job_v12(id TEXT);";
        await cmd.ExecuteNonQueryAsync();
    }

    await Seed(path);
    var result = await bootstrapper.BootstrapAsync(path);
    Assert(result.SchemaVersion == 15 && !result.DatabaseReset, "Upgrade v11 in place.");
    var fresh = await ReadSchemaObjectsAsync(freshPath);
    var schema = await ReadSchemaObjectsAsync(path);
    Assert(fresh.Count == schema.Count && fresh.All(p => schema.GetValueOrDefault(p.Key) == p.Value), "Analysis migration matches fresh SQL.");
    await using (var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT result_text FROM Job JOIN ExecutionBinding ON job_id=id JOIN ActiveSlotOccupancy USING(job_id) WHERE Job.id='j'";
        Assert((string?)await cmd.ExecuteScalarAsync() == "kept", "Rebuilding Job keeps results, bindings and occupancies.");
        cmd.CommandText = "PRAGMA foreign_key_check";
        Assert(await cmd.ExecuteScalarAsync() is null, "Rebuilt Job retains foreign-key integrity.");
    }
    var backup = Directory.GetFiles(root, "v11-analysis.db.pre-analytics-analysis-v12-*.bak").Single();
    await using (var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(backup)))
    {
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 11, "Backup preserves schema version 11.");
    }

    var broken = Path.Combine(root, "broken-v11-analysis.db");
    await Seed(broken, conflict: true);
    var before = await ReadSchemaObjectsAsync(broken);
    var failed = false;
    try { await bootstrapper.BootstrapAsync(broken); } catch (SqliteException) { failed = true; }
    var after = await ReadSchemaObjectsAsync(broken);
    Assert(failed && before.Count == after.Count && before.All(p => after.GetValueOrDefault(p.Key) == p.Value),
        "Conflicting v12 rebuild rolls back the report table and preserves Job.");
    Console.WriteLine("AI Analytics v11 -> v12: Job, bindings, occupancies, backup and rollback PASS");
}

static async Task VerifyAnalyticsUpgradeAsync(string root, SqliteDatabaseBootstrapper bootstrapper, string freshPath)
{
    async Task Seed(string path)
    {
        await using var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
        await db.OpenAsync(); await using var cmd = db.CreateCommand();
        cmd.CommandText = await File.ReadAllTextAsync("tests/Fraylo.FinalSchema.SmokeChecks/Fixtures/schema-v10.sql") +
            "INSERT INTO AppSettings(key,value,updated_at_utc) VALUES ('analytics-kept','kept','now');";
        await cmd.ExecuteNonQueryAsync();
    }
    var path = Path.Combine(root, "v10.db"); await Seed(path);
    var upgraded = await bootstrapper.BootstrapAsync(path);
    Assert(upgraded.SchemaVersion == 15 && !upgraded.DatabaseReset, "Analytics upgrade must preserve v10.");
    var fresh = await ReadSchemaObjectsAsync(freshPath); var schema = await ReadSchemaObjectsAsync(path);
    Assert(fresh.Count == schema.Count && fresh.All(p => schema.GetValueOrDefault(p.Key) == p.Value), "Analytics v10 upgrade and fresh schema parity.");
    await using (var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await db.OpenAsync(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM AppSettings WHERE key='analytics-kept'";
        Assert((string?)await cmd.ExecuteScalarAsync() == "kept", "Analytics upgrade keeps existing data.");
    }
    var backups = Directory.GetFiles(root, "v10.db.pre-facebook-analytics-v11-*.bak");
    Assert(backups.Length == 1, "Analytics upgrade creates one backup.");
    await using (var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(backups[0])))
    {
        await db.OpenAsync(); await using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version";
        Assert(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 10, "Analytics backup keeps original schema.");
    }
    var broken = Path.Combine(root, "broken-v10.db"); await Seed(broken);
    await using (var db = new SqliteConnection(DatabasePathHelper.BuildConnectionString(broken)))
    {
        await db.OpenAsync(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE FacebookAnalyticsCapture (Id TEXT PRIMARY KEY)"; await cmd.ExecuteNonQueryAsync();
    }
    var before = await ReadSchemaObjectsAsync(broken); var failed = false;
    try { await bootstrapper.BootstrapAsync(broken); } catch (SqliteException) { failed = true; }
    var after = await ReadSchemaObjectsAsync(broken);
    Assert(failed && before.Count == after.Count && before.All(p => after.GetValueOrDefault(p.Key) == p.Value), "Analytics migration failure rolls back preceding CREATEs.");
    Console.WriteLine("Facebook Analytics v10 -> v11 preservation/backup/schema parity/rollback: PASS");
}

// ALTER TABLE appends columns and SQLite reformats CREATE SQL; compare definitions independently of column order/whitespace.
static string CanonicalSchema(string sql)
{
    var text = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();
    if (!text.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)) return text;
    var start = text.IndexOf('('); var end = text.LastIndexOf(')');
    var parts = new List<string>(); var depth = 0; var from = start + 1;
    for (var i = from; i < end; i++)
    {
        if (text[i] == '(') depth++;
        if (text[i] == ')') depth--;
        if (text[i] == ',' && depth == 0) { parts.Add(text[from..i].Trim()); from = i + 1; }
    }
    parts.Add(text[from..end].Trim());
    return text[..start].Trim() + "(" + string.Join(",", parts.Order(StringComparer.Ordinal)) + ")";
}

static async Task VerifyPostsUpgradeAsync(string root, SqliteDatabaseBootstrapper bootstrapper, string freshPath)
{
    async Task SeedV9(string path)
    {
        await SeedVersion8WithTaskAsync(path);
        await using var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync("tests/Fraylo.FinalSchema.SmokeChecks/Fixtures/facebook-v9.sql") + "PRAGMA user_version=9;";
        await command.ExecuteNonQueryAsync();
    }
    var path = Path.Combine(root, "v9.db");
    await SeedV9(path);
    var result = await bootstrapper.BootstrapAsync(path);
    Assert(result.SchemaVersion == 15 && !result.DatabaseReset, "v9 -> v10 must preserve data.");
    var fresh = await ReadSchemaObjectsAsync(freshPath); var upgraded = await ReadSchemaObjectsAsync(path);
    Assert(fresh.Count == upgraded.Count && fresh.All(p => upgraded.GetValueOrDefault(p.Key) == p.Value), "v9 upgrade schema parity.");
    Assert(Directory.GetFiles(root, "v9.db.pre-facebook-v10-*.bak").Length == 1, "v9 upgrade has a backup.");
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(path)))
    {
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text_snapshot || ':' || state || ':' || revision FROM FacebookPostTask WHERE id='kept-task';";
        Assert((string?)await command.ExecuteScalarAsync() == "Bài giữ nguyên:Published:3", "v10 keeps task content/state/revision.");
    }
    var broken = Path.Combine(root, "broken-v9.db"); await SeedV9(broken);
    await using (var connection = new SqliteConnection(DatabasePathHelper.BuildConnectionString(broken)))
    {
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE FacebookPostBatch ADD COLUMN paused INTEGER NOT NULL DEFAULT 0;";
        await command.ExecuteNonQueryAsync();
    }
    var original = await ReadSchemaObjectsAsync(broken); var failed = false;
    try { await bootstrapper.BootstrapAsync(broken); } catch (SqliteException) { failed = true; }
    var after = await ReadSchemaObjectsAsync(broken);
    Assert(failed && original.Count == after.Count && original.All(p => after.GetValueOrDefault(p.Key) == p.Value), "v10 failure rolls back the added name column.");
    Console.WriteLine("Facebook v9 -> v10 preservation/parity/backup/rollback: PASS");
}
