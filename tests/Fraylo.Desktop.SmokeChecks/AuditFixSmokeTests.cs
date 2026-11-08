using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Fraylo.Application.Analytics;
using Fraylo.Application.Content;
using Fraylo.Application.Jobs;
using Fraylo.Application.Scenes;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Analytics;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Scenes;
using Fraylo.Desktop.Hosting;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Fraylo.Desktop.SmokeChecks;

internal static class AuditFixSmokeTests
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Fraylo-audit-fixes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite($"Data Source={Path.Combine(root, "test.db")}").Options;
            await using (var db = new FrayloDbContext(options)) await db.Database.EnsureCreatedAsync();
            await SceneInputsAsync(options, root);
            await CalendarPeriodsAsync(options);
            InstallationLocks();
            Console.WriteLine("Audit fixes: Scene inputs, calendar periods and cross-process installation lock PASS.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
    }

    private static async Task SceneInputsAsync(DbContextOptions<FrayloDbContext> options, string root)
    {
        var project = Guid.NewGuid().ToString("D");
        var scene = Guid.NewGuid().ToString("D");
        var image = Guid.NewGuid().ToString("N");
        var audio = Guid.NewGuid().ToString("N");
        var item = new VideoContentItemDto(scene, project, scene, 1, "Scene", "", "", "OLD PROMPT", "Caption", image, null, audio, "ReadyForVideo", "now", "now");
        await using (var db = new FrayloDbContext(options))
        {
            db.Projects.Add(new() { Id = project, Name = "Audit", CreatedAtUtc = "now", UpdatedAtUtc = "now" });
            db.Assets.AddRange(new Asset { Id = image, AssetType = "Image", StorageRootId = "root-default-images", RelativePath = "image.png", Sha256Hash = new string('a', 64), CreatedAtUtc = "now" },
                new Asset { Id = audio, AssetType = "Audio", StorageRootId = "root-internal-audio", RelativePath = "old.wav", Sha256Hash = new string('b', 64), CreatedAtUtc = "now" });
            db.Scenes.Add(new() { Id = scene, ProjectId = project, SequenceOrder = 1, Name = "Scene", PromptText = "OLD PROMPT", ImageAssetId = image, AudioAssetId = audio, TargetDurationSeconds = 10, CreatedAtUtc = "now", UpdatedAtUtc = "now" });
            db.AppSettings.Add(new() { Key = "project.video_content." + project, Value = JsonSerializer.Serialize(new[] { item }), UpdatedAtUtc = "now" });
            await db.SaveChangesAsync();
        }
        await new SceneService(options).UpdateAsync(new SceneUpdateRequest(project, scene, PromptText: "NEW PROMPT", AudioAssetId: ""));
        var settings = new SettingsService(options, new StorageRootRegistry(options, root));
        await using var scheduler = new JobScheduler(options, new JobReservation(options), new NotExecuted());
        var workflow = new ProjectWorkflowService(options, new JobService(options, new JobSnapshotFactory(settings, dbOptions: options), scheduler));
        var listed = (await workflow.ListVideoContentItemsAsync(project)).Single();
        Check(listed.VideoPrompt == "NEW PROMPT" && listed.AudioAssetId == null && listed.TikTokContent == "Caption", "Scene is authoritative, caption preserved");
        await workflow.CreateSceneVideoAsync(new(project, scene));
        await using (var db = new FrayloDbContext(options))
        {
            var job = await db.Jobs.SingleAsync();
            using var prompt = JsonDocument.Parse(job.PromptSnapshotText!);
            using var snapshot = JsonDocument.Parse(job.ConfigSnapshotJson!);
            Check(prompt.RootElement.GetProperty("renderedText").GetString() == "NEW PROMPT" &&
                snapshot.RootElement.GetProperty("MediaSnapshot").GetProperty("AudioAssetId").ValueKind == JsonValueKind.Null, "render snapshots current prompt and detached audio");
        }
    }

    private static async Task CalendarPeriodsAsync(DbContextOptions<FrayloDbContext> options)
    {
        var today = DateTime.UtcNow.Date;
        await using (var db = new FrayloDbContext(options))
        {
            db.BrowserProfiles.Add(new() { Id = "audit-tiktok", Kind = "TikTok", ProviderKey = "tiktok" });
            db.AnalyticsSnapshots.Add(new() { Id = "audit-snapshot", AccountId = "audit-tiktok", Source = "Fixture", CreatedAtUtc = today });
            for (var day = 0; day < 32; day++) db.Set<TikTokAnalyticsDailyRecord>().Add(new()
            {
                Id = Guid.NewGuid().ToString("N"),
                SnapshotId = "audit-snapshot",
                AccountId = "audit-tiktok",
                DataType = "overview",
                PeriodDateUtc = today.AddDays(-day),
                VideoViews = 100,
                CapturedAtUtc = today
            });
            await db.SaveChangesAsync();
        }
        var analytics = new AnalyticsService(options);
        foreach (var period in new[] { 1, 7, 30 })
        {
            var summary = await analytics.GetSummaryAsync(new(PeriodDays: period, AccountId: "audit-tiktok"));
            var graph = await analytics.GetTimeseriesAsync(new("views", period, "overview", "audit-tiktok"));
            Check(summary.TotalViews == period * 100 && graph.Count == period && graph.Sum(p => p.Value) == summary.TotalViews, $"summary/graph agree for {period} days");
        }
        Check((await analytics.GetSummaryAsync(new(AccountId: "audit-tiktok"))).TotalViews == 3200, "unbounded period preserves every day");
    }

    private static void InstallationLocks()
    {
        var name = InstallationLock.Name + ".test." + Guid.NewGuid().ToString("N");
        using (var held = InstallationLock.TryAcquire(name))
        {
            Check(held != null, "first instance acquires installation lock");
            Check(Child("audit-lock-probe", name) == 2, "other process cannot start while app/updater holds lock");
        }
        Check(Child("audit-lock-probe", name) == 0, "lock released before updated app starts");
        using var observer = new System.Threading.Mutex(false, name);
        Check(Child("audit-lock-abandon", name) == 0, "crash fixture exits");
        using var recovered = InstallationLock.TryAcquire(name);
        Check(recovered != null, "abandoned lock recovers after process crash");
    }

    public static int ProbeLock(string name)
    {
        using var held = InstallationLock.TryAcquire(name);
        return held == null ? 2 : 0;
    }

    private static int Child(string command, string name)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(command); start.ArgumentList.Add(name);
        using var child = Process.Start(start)!;
        if (!child.WaitForExit(10_000)) { child.Kill(); throw new TimeoutException("Installation lock fixture timed out."); }
        return child.ExitCode;
    }

    private static void Check(bool ok, string scenario) { if (!ok) throw new Exception(scenario); }

    private sealed class NotExecuted : IJobExecutor
    {
        public Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, Func<Task> before, Func<int, string, Task>? progress, CancellationToken ct) => throw new InvalidOperationException("Fixture must not call a provider.");
    }
}
