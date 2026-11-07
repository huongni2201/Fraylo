using System.IO;
using System.Text.Json;
using Fraylo.Application.Analytics;
using Fraylo.Application.Jobs;
using Fraylo.Application.Providers;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Analytics;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;
using Fraylo.Desktop.Bridge;
using Fraylo.Desktop.Bridge.Handlers;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fraylo.Desktop.SmokeChecks;

internal static class AnalyticsAnalysisSmokeTests
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fraylo-ai-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "test.db");
            await new SqliteDatabaseBootstrapper().BootstrapAsync(path);
            var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite(DatabasePathHelper.BuildConnectionString(path)).Options;
            var registry = new ProviderRegistry();
            var adapter = new FixtureAdapter();
            registry.RegisterAdapter(adapter);
            await using var scheduler = new JobScheduler(options, new JobReservation(options), new ProductionJobExecutor(registry));
            var settings = new SettingsService(options, new StorageRootRegistry(options));
            var jobs = new JobService(options, new JobSnapshotFactory(settings), scheduler);
            var analysis = new AnalyticsAnalysisService(options, registry, jobs, scheduler);
            var importer = new AnalyticsService(options, analysis: analysis);
            var dispatcher = new BridgeDispatcher();
            new AnalyticsAnalysisHandlers(analysis).Register(dispatcher);
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using (var db = new FrayloDbContext(options))
            {
                db.BrowserProfiles.AddRange(
                    new BrowserProfile { Id = "a", Kind = "TikTok", ProviderKey = "tiktok" },
                    new BrowserProfile { Id = "b", Kind = "TikTok", ProviderKey = "tiktok" },
                    new BrowserProfile { Id = "ai-profile", Kind = "ChatGPT", ProviderKey = ProviderKeys.ChatGPTWeb,
                        AccountIdentity = "test@example.invalid", SessionStatus = "Connected" });
                db.ProviderAccounts.Add(new ProviderAccount { Id = "ai", ProfileId = "ai-profile", Enabled = false,
                    VerifiedIdentity = "test@example.invalid", ProviderKey = ProviderKeys.ChatGPTWeb });
                db.AppSettings.Add(new AppSetting { Key = SettingsService.KeyAnalyticsProvider, Value = ProviderKeys.ChatGPTWeb, UpdatedAtUtc = now });
                await db.SaveChangesAsync();
            }

            async Task<JsonElement> List(string account = "a")
            {
                var result = await dispatcher.DispatchAsync(new RequestEnvelope(Guid.NewGuid(), "request",
                    "analytics.analysis.list", JsonSerializer.SerializeToElement(new { platform = "tiktok", accountId = account }),
                    DateTimeOffset.UtcNow, BridgeContract.Version), default);
                Check(result.Success, result.Error?.Message ?? "List via bridge");
                return result.Data!.Value.GetProperty("reports");
            }

            Check((await List()).GetArrayLength() == 0, "Opening Analytics never enqueues.");
            var csv = "video_key,title,views,likes\n" + string.Join("\n", Enumerable.Range(1, 75)
                .Select(i => $"video-{i},Bài {i} https://example.invalid/private?token=removed,{i},0"));
            var source = await importer.ImportDataAsync(new("a", "csv", csv));
            var queued = (await List()).EnumerateArray().Single();
            var id = queued.GetProperty("id").GetString()!;
            Check(queued.GetProperty("sourceId").GetString() == source.SnapshotId &&
                queued.GetProperty("state").GetString() == JobState.Queued, "Import enqueues once for the saved source.");
            Check((await List("b")).GetArrayLength() == 0, "Other account sees no report.");
            var wrongScope = await dispatcher.DispatchAsync(new RequestEnvelope(Guid.NewGuid(), "request",
                "analytics.analysis.get", JsonSerializer.SerializeToElement(new { platform = "tiktok", accountId = "b", reportId = id }),
                DateTimeOffset.UtcNow, BridgeContract.Version), default);
            Check(!wrongScope.Success, "Report ID does not bypass scope.");
            Check(!await scheduler.DispatchNextSingleJobAsync(), "A disabled AI account cannot receive the job.");
            await using (var db = new FrayloDbContext(options))
            {
                var waiting = await db.Jobs.SingleAsync();
                Check(waiting.Id == id && waiting.State == JobState.Queued && waiting.ReasonCode == "provider_account_required" &&
                    waiting.Attempts == 0 && waiting.SideEffectSubmitted == 0 && adapter.Calls == 0,
                    "The same job waits for an eligible account without consuming an attempt or sending data.");
                (await db.ProviderAccounts.SingleAsync()).Enabled = true;
                await db.SaveChangesAsync();
            }
            Check(await scheduler.DispatchNextSingleJobAsync(), "Shared scheduler reserves the Analytics job.");
            await scheduler.WaitForWorkerCompletionAsync(id, TimeSpan.FromSeconds(10));
            var completed = (await List()).EnumerateArray().Single();
            Check(completed.GetProperty("state").GetString() == JobState.Succeeded && adapter.Calls == 1,
                "Fake adapter reply reaches SQLite and bridge once.");
            var input = completed.GetProperty("input");
            Check(input.GetProperty("totalRecords").GetInt32() == 75 && input.GetProperty("omittedRecords").GetInt32() > 0 &&
                input.GetProperty("totals").EnumerateArray().Any(t => t.GetProperty("metric").GetString() == "views" &&
                    t.GetProperty("value").GetInt32() == 2850), "Totals use all rows before sampling.");
            Check(input.GetProperty("sample").EnumerateArray().Any(r => r.GetProperty("metrics").GetProperty("likes").GetProperty("value").GetInt32() == 0),
                "Zero survives normalization.");
            Check(adapter.Prompt is { Length: <= 8000 } && !adapter.Prompt.Contains("token=removed") &&
                !adapter.Prompt.Contains("test@example.invalid"), "Payload is bounded and excludes links and account identity.");
            await using (var db = new FrayloDbContext(options))
            {
                var job = await db.Jobs.SingleAsync();
                Check(job.Id == id && job.ReasonCode is null && job.Attempts == 1 &&
                    job.ProjectId is null && job.SceneId is null && job.AnalyticsReportId == id &&
                    await db.Projects.CountAsync() == 0, "Report owns a real standalone job.");
            }

            adapter.Invalid = true;
            var second = await importer.ImportDataAsync(new("a", "csv", "video_key,title,views\nvideo-x,Bài mới,8"));
            var secondId = (await List()).EnumerateArray().Single(r => r.GetProperty("sourceId").GetString() == second.SnapshotId)
                .GetProperty("id").GetString()!;
            Check(await scheduler.DispatchNextSingleJobAsync(), "Second source gets its own job.");
            await scheduler.WaitForWorkerCompletionAsync(secondId, TimeSpan.FromSeconds(10));
            var reports = await List();
            Check(reports.EnumerateArray().Any(r => r.GetProperty("state").GetString() == JobState.Succeeded) &&
                reports.EnumerateArray().Any(r => r.GetProperty("reason").GetString() == "InvalidAnalysisResult"),
                "Fabricated evidence is rejected and previous report remains.");
            Check(!await scheduler.DispatchNextSingleJobAsync() && adapter.Calls == 2, "Invalid output does not auto resend.");

            var third = await importer.ImportDataAsync(new("a", "csv", "video_key,title,views\nvideo-y,Bài khác,0"));
            await analysis.HoldUnsentOnStartupAsync(default);
            var held = (await List()).EnumerateArray().Single(r => r.GetProperty("sourceId").GetString() == third.SnapshotId);
            Check(held.GetProperty("reason").GetString() == "AnalysisRestarted" && !await scheduler.DispatchNextSingleJobAsync(),
                "An unsent job after restart waits for continuation.");
            var token = Guid.NewGuid().ToString("N");
            var request = new AnalyticsAnalysisRequest("tiktok", "b", SourceId: source.SnapshotId, ClientRequestId: token);
            try { await analysis.StartAsync(request, false, default); throw new Exception("Expected cross-account rejection."); }
            catch (InvalidOperationException) { }
            var retry = new AnalyticsAnalysisRequest("tiktok", "a", ReportId: secondId, ClientRequestId: token);
            await Task.WhenAll(analysis.StartAsync(retry, true, default), analysis.StartAsync(retry, true, default));
            Check((await List()).EnumerateArray().Count(r => r.GetProperty("sourceId").GetString() == second.SnapshotId) == 2,
                "Concurrent replay of a manual retry creates one new report.");
            Console.WriteLine("Analytics AI SQLite -> scheduler -> adapter -> bridge: PASS");
            return 0;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class FixtureAdapter : IProviderAdapter
    {
        public string ProviderKey => ProviderKeys.ChatGPTWeb;
        public int Calls { get; private set; }
        public string? Prompt { get; private set; }
        public bool Invalid { get; set; }
        public IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities() => [
            new(ProviderKey, JobTaskType.AnalyticsAnalysis, ProviderVerificationStatus.Verified, true, false, false, 8000, null, null, 1)
        ];
        public Task<ProviderTaskCapabilityDto?> GetCapabilityForTaskAsync(string taskType, CancellationToken ct = default) =>
            Task.FromResult(GetCapabilities().SingleOrDefault(c => c.TaskType == taskType));
        public async Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, Func<Task> before,
            Func<int, string, Task>? progress, CancellationToken ct = default)
        {
            Calls++;
            using var prompt = JsonDocument.Parse(context.PromptSnapshotText!);
            Prompt = prompt.RootElement.GetProperty("renderedText").GetString();
            await before();
            var item = new { text = "Thử một chủ đề và theo dõi lượt xem", evidence = new[] { Invalid ? "not-in-snapshot" : "T1" } };
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, summary = "Báo cáo fixture", highlights = new[] { item }, improvements = Array.Empty<object>(),
                recommendations = new[] { item, item, item }, limitations = new[] { "Mẫu số liệu thử nghiệm" }
            });
            return new JobExecutionResult(true, ResultText: json, SideEffectSubmitted: true);
        }
    }
}
