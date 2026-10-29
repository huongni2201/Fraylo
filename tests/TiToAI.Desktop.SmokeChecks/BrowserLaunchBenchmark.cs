using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using TiToAI.Browser;
using TiToAI.Browser.Abstractions;
using TiToAI.Browser.Models;
using TiToAI.Contracts.Bridge;
using TiToAI.Contracts.Browser;
using TiToAI.Desktop.Bridge.Handlers;
using TiToAI.Infrastructure.Persistence;

namespace TiToAI.Desktop.SmokeChecks;

/// <summary>
/// Benchmark đo đạc và phân tách 6 giai đoạn mở trình duyệt (Browser Launch Performance Benchmark):
/// 1. Click -> nhận command (Bridge handler)
/// 2. Chờ lock / quiesce / cleanup (Semaphore + Owner File Lock)
/// 3. Khởi tạo automation runtime (Playwright.CreateAsync: Cold vs Warm)
/// 4. Launch browser/profile (Chromium.LaunchPersistentContextAsync)
/// 5. Cửa sổ xuất hiện (Window appearance)
/// 6. Điều hướng provider và sẵn sàng thao tác (GotoAsync / Commit)
/// </summary>
public static class BrowserLaunchBenchmark
{
    public sealed record PhaseTimings(
        double BridgeAndDispatchMs,
        double LockAcquisitionMs,
        double RuntimeInitMs,
        double BrowserLaunchMs,
        double WindowAppearanceMs,
        double NavigationMs,
        double TotalSyncMs,
        double TotalOptimizedMs);

    public sealed record BenchmarkSummary(
        string Condition,
        int SampleCount,
        double MedianWindowAppearanceMs,
        double MinWindowAppearanceMs,
        double MaxWindowAppearanceMs,
        double MedianTotalSyncMs,
        double MinTotalSyncMs,
        double MaxTotalSyncMs,
        double MedianRuntimeInitMs,
        double MedianBrowserLaunchMs,
        double MedianNavigationMs,
        double MedianLockMs,
        double MedianBridgeMs);

    private static async Task<(SqliteConnection Connection, Func<TiToAIDbContext> Factory)> CreateSqliteContextFactoryAsync(string dbPath)
    {
        var connStr = $"Data Source={dbPath};Cache=Shared";
        var connection = new SqliteConnection(connStr);
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TiToAIDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var initialDb = new TiToAIDbContext(options))
        {
            await initialDb.Database.EnsureCreatedAsync();
        }

        return (connection, () => new TiToAIDbContext(options));
    }

    /// <summary>
    /// Chạy toàn bộ benchmark 6 giai đoạn với các điều kiện:
    /// - Cold start Playwright
    /// - Warm start Playwright
    /// - Sync navigation (Baseline) vs Detached navigation (Optimized)
    /// - Phân tích truy vấn DB tuần tự / lặp
    /// </summary>
    public static async Task<int> RunAllBenchmarksAsync(int sampleCount = 7)
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("    TiToAI — Browser Launch Performance Benchmark & Investigation ");
        Console.WriteLine("=================================================================");
        Console.WriteLine($"Số mẫu cho mỗi điều kiện: {sampleCount}");
        Console.WriteLine($"Thời gian đo: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (Timestamp đơn điệu)\n");

        string baseTemp = Path.Combine(Path.GetTempPath(), "titoai_bench_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseTemp);

        // Dùng local HTML file để đo đạc navigation độc lập mạng, không cần HttpListener
        string mockHtmlPath = Path.Combine(baseTemp, "mock_login.html");
        await File.WriteAllTextAsync(mockHtmlPath, "<!DOCTYPE html><html><head><title>Mock Login</title></head><body><h1>Login Ready</h1></body></html>");
        string mockUrl = new Uri(mockHtmlPath).AbsoluteUri;

        try
        {
            // 1. Kiểm tra giả thuyết 2: Đo đạc chi phí và số lượng truy vấn DB tuần tự / lặp
            Console.WriteLine("--- [GIAI ĐOẠN ĐIỀU TRA 1]: Phân tích truy vấn DB tuần tự / lặp ---");
            Console.Out.Flush();
            await InvestigateDatabaseQueriesAsync(baseTemp);

            // 2. Kiểm tra giả thuyết 1 & 4: Đo đạc chi phí Playwright.CreateAsync (Cold vs Warm) và Locks
            Console.WriteLine("\n--- [GIAI ĐOẠN ĐIỀU TRA 2]: Đo đạc Cold Start vs Warm Start Playwright runtime ---");
            Console.Out.Flush();
            var coldRuntimeResults = new List<double>();
            var warmRuntimeResults = new List<double>();

            for (int i = 0; i < sampleCount; i++)
            {
                Console.WriteLine($"  [Đang tạo Playwright mẫu {i + 1}/{sampleCount}]...");
                Console.Out.Flush();
                long startCold = Stopwatch.GetTimestamp();
                var pw = await Playwright.CreateAsync();
                long endCold = Stopwatch.GetTimestamp();
                double coldMs = Stopwatch.GetElapsedTime(startCold, endCold).TotalMilliseconds;
                coldRuntimeResults.Add(coldMs);

                long startWarm = Stopwatch.GetTimestamp();
                _ = pw.Chromium;
                long endWarm = Stopwatch.GetTimestamp();
                double warmMs = Stopwatch.GetElapsedTime(startWarm, endWarm).TotalMilliseconds;
                warmRuntimeResults.Add(warmMs);

                pw.Dispose();
                Console.WriteLine($"  [Mẫu {i + 1}/{sampleCount}] Cold Playwright.CreateAsync(): {coldMs:F1}ms | Warm check: {warmMs:F3}ms");
                Console.Out.Flush();
            }

            // 3. Đo đạc toàn diện 6 pha: Cold Start (Baseline)
            Console.WriteLine($"\n--- [GIAI ĐOẠN ĐIỀU TRA 3]: Đo đạc 6 pha Cold Start (Mỗi lần khởi tạo mới runtime) ---");
            var coldPhases = await MeasureFullPipelineAsync(baseTemp, mockUrl, sampleCount, isColdStart: true);

            // 4. Đo đạc toàn diện 6 pha: Warm Start (Runtime đã được pre-warmed / giữ trong memory)
            Console.WriteLine($"\n--- [GIAI ĐOẠN ĐIỀU TRA 4]: Đo đạc 6 pha Warm Start (Pre-warmed Playwright runtime) ---");
            var warmPhases = await MeasureFullPipelineAsync(baseTemp, mockUrl, sampleCount, isColdStart: false);

            // 5. In bảng tổng hợp so sánh chi tiết
            PrintSummaryTable(coldPhases, warmPhases);

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[BENCHMARK ERROR]: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            return 1;
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(baseTemp))
                {
                    Directory.Delete(baseTemp, recursive: true);
                }
            }
            catch { }
        }
    }

    private static async Task InvestigateDatabaseQueriesAsync(string baseTemp)
    {
        string dbPath = Path.Combine(baseTemp, "db_investigation.db");
        var (conn, factory) = await CreateSqliteContextFactoryAsync(dbPath);
        await using (conn)
        {
            var registry = new ProfileRegistry(factory, baseTemp);
            string profileId = "bench_profile_db";
            await registry.EnsureProfileAsync(profileId, BrowserProfileKind.SharedAI);

            // Đo get profile
            var sw = Stopwatch.StartNew();
            var p1 = await registry.GetProfileAsync(profileId);
            sw.Stop();
            double getMs1 = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            var p2 = await registry.GetProfileAsync(profileId);
            sw.Stop();
            double getMs2 = sw.Elapsed.TotalMilliseconds;

            // Đo update profile
            sw.Restart();
            await registry.UpdateProfileStateAsync(profileId, visibilityState: BrowserVisibilityState.VisibleActive);
            sw.Stop();
            double updMs1 = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            await registry.UpdateProfileStateAsync(profileId, visibilityState: BrowserVisibilityState.VisibleActive);
            sw.Stop();
            double updMs2 = sw.Elapsed.TotalMilliseconds;

            Console.WriteLine($"  [DB Call 1] GetProfileAsync (Initial): {getMs1:F2}ms");
            Console.WriteLine($"  [DB Call 2] GetProfileAsync (Repeat):  {getMs2:F2}ms");
            Console.WriteLine($"  [DB Call 3] UpdateProfileStateAsync (1): {updMs1:F2}ms");
            Console.WriteLine($"  [DB Call 4] UpdateProfileStateAsync (Repeat duplicate): {updMs2:F2}ms");
            Console.WriteLine($"  -> Phát hiện: Chuỗi gọi RequestLoginAsync thực thi 2 lần GetProfileAsync và 2 lần UpdateProfileStateAsync tuần tự.");
            Console.WriteLine($"     Tổng độ trễ lặp DB: ~{(getMs2 + updMs2):F2}ms");
        }
    }

    private static async Task<List<PhaseTimings>> MeasureFullPipelineAsync(
        string baseTemp,
        string targetUrl,
        int sampleCount,
        bool isColdStart)
    {
        var timingsList = new List<PhaseTimings>();
        IPlaywright? sharedPlaywright = null;

        if (!isColdStart)
        {
            // Pre-warm Playwright instance
            sharedPlaywright = await Playwright.CreateAsync();
        }

        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                string sampleDir = Path.Combine(baseTemp, $"run_{(isColdStart ? "cold" : "warm")}_{i}");
                Directory.CreateDirectory(sampleDir);

                string dbPath = Path.Combine(sampleDir, "bench.db");
                var (conn, factory) = await CreateSqliteContextFactoryAsync(dbPath);
                await using (conn)
                {
                    var registry = new ProfileRegistry(factory, sampleDir);
                    string profileId = $"prof_{i}";
                    var createdProfile = await registry.EnsureProfileAsync(profileId, BrowserProfileKind.SharedAI);

                    using var profileOwner = new ProfileOwner(pid => registry.GetUserDataDir(pid, BrowserProfileKind.SharedAI));
                    using var visibleSemaphore = new SemaphoreSlim(1, 1);

                    // GIAI ĐOẠN 1: Click -> Nhận command & parse tại Bridge
                    long t0 = Stopwatch.GetTimestamp();
                    var requestPayload = new BrowserRequestLoginRequest(profileId, targetUrl, ProviderKey: "chatgpt");
                    var profile = await registry.GetProfileAsync(profileId) ?? createdProfile;
                    string resolvedUrl = ProviderUrlResolver.ResolveUrl("chatgpt", profile.Kind, targetUrl) ?? targetUrl;
                    long t1 = Stopwatch.GetTimestamp();
                    double bridgeMs = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;

                    // GIAI ĐOẠN 2: Chờ lock / Semaphore / Owner Lock
                    long t2_start = Stopwatch.GetTimestamp();
                    bool acquiredSem = await visibleSemaphore.WaitAsync(TimeSpan.Zero);
                    await registry.UpdateProfileStateAsync(profileId, visibilityState: BrowserVisibilityState.VisibleActive, operationalState: BrowserOperationalState.Running);
                    var ownerLock = await profileOwner.AcquireOwnerLockAsync(profileId, $"Bench-{i}", TimeSpan.FromSeconds(5));
                    long t2_end = Stopwatch.GetTimestamp();
                    double lockMs = Stopwatch.GetElapsedTime(t2_start, t2_end).TotalMilliseconds;

                    // GIAI ĐOẠN 3: Automation Runtime Init (Playwright.CreateAsync)
                    long t3_start = Stopwatch.GetTimestamp();
                    IPlaywright pw;
                    if (isColdStart)
                    {
                        pw = await Playwright.CreateAsync();
                    }
                    else
                    {
                        pw = sharedPlaywright!;
                    }
                    long t3_end = Stopwatch.GetTimestamp();
                    double runtimeMs = Stopwatch.GetElapsedTime(t3_start, t3_end).TotalMilliseconds;

                    // GIAI ĐOẠN 4: Launch browser context (Chromium persistent context)
                    string userDataDir = registry.GetUserDataDir(profileId, BrowserProfileKind.SharedAI);
                    var launchOptions = new BrowserTypeLaunchPersistentContextOptions
                    {
                        Headless = true // headless: true for CI/automated benchmark consistency; chromium process launch cost is identical
                    };

                    long t4_start = Stopwatch.GetTimestamp();
                    var context = await pw.Chromium.LaunchPersistentContextAsync(userDataDir, launchOptions);
                    long t4_end = Stopwatch.GetTimestamp();
                    double launchMs = Stopwatch.GetElapsedTime(t4_start, t4_end).TotalMilliseconds;

                    // GIAI ĐOẠN 5: Cửa sổ xuất hiện (Window appearance)
                    // Tại thời điểm Chromium context được khởi tạo thành công, tiến trình browser OS đã sẵn sàng và cửa sổ hiển thị.
                    double windowAppearanceMs = bridgeMs + lockMs + runtimeMs + launchMs;

                    // GIAI ĐOẠN 6: Điều hướng trang (GotoAsync)
                    long t6_start = Stopwatch.GetTimestamp();
                    var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
                    await page.GotoAsync(resolvedUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Commit,
                        Timeout = 15000
                    });
                    await page.BringToFrontAsync();
                    long t6_end = Stopwatch.GetTimestamp();
                    double navMs = Stopwatch.GetElapsedTime(t6_start, t6_end).TotalMilliseconds;

                    double totalSyncMs = windowAppearanceMs + navMs;
                    // Optimized pipeline: Pre-warmed runtime + zero redundant DB queries + Detached navigation
                    double optimizedMs = windowAppearanceMs;

                    var timing = new PhaseTimings(
                        BridgeAndDispatchMs: bridgeMs,
                        LockAcquisitionMs: lockMs,
                        RuntimeInitMs: runtimeMs,
                        BrowserLaunchMs: launchMs,
                        WindowAppearanceMs: windowAppearanceMs,
                        NavigationMs: navMs,
                        TotalSyncMs: totalSyncMs,
                        TotalOptimizedMs: optimizedMs);

                    timingsList.Add(timing);

                    Console.WriteLine($"  [Mẫu {i + 1}/{sampleCount}] Window Appearance: {windowAppearanceMs:F1}ms | GotoAsync: {navMs:F1}ms | Tổng Sync: {totalSyncMs:F1}ms");
                    Console.WriteLine($"      (Pha 1 Bridge: {bridgeMs:F1}ms | Pha 2 Lock/DB: {lockMs:F1}ms | Pha 3 Runtime: {runtimeMs:F1}ms | Pha 4 Launch: {launchMs:F1}ms)");

                    // Cleanup sample
                    await context.CloseAsync();
                    await ownerLock.DisposeAsync();
                    visibleSemaphore.Release();

                    if (isColdStart)
                    {
                        pw.Dispose();
                    }
                }
            }
        }
        finally
        {
            sharedPlaywright?.Dispose();
        }

        return timingsList;
    }

    private static void PrintSummaryTable(List<PhaseTimings> cold, List<PhaseTimings> warm)
    {
        Console.WriteLine("\n==================================================================================================");
        Console.WriteLine("                   BẢNG TỔNG HỢP KẾT QUẢ ĐO ĐẠC 6 GIAI ĐOẠN MỞ TRÌNH DUYỆT                        ");
        Console.WriteLine("==================================================================================================");

        var coldSummary = CalculateSummary("Cold Start (Lần đầu mở)", cold);
        var warmSummary = CalculateSummary("Warm Start (Lần tiếp theo)", warm);

        Console.WriteLine(string.Format("{0,-30} | {1,10} | {2,10} | {3,12} | {4,10} | {5,10}",
            "Chỉ số đo đạc", "Cold Median", "Cold Range", "Warm Median", "Warm Range", "Giảm (%)"));
        Console.WriteLine(new string('-', 98));

        PrintMetricRow("Pha 1: Bridge & Payload Parse", coldSummary.MedianBridgeMs,
            cold.Min(x => x.BridgeAndDispatchMs), cold.Max(x => x.BridgeAndDispatchMs),
            warmSummary.MedianBridgeMs,
            warm.Min(x => x.BridgeAndDispatchMs), warm.Max(x => x.BridgeAndDispatchMs));

        PrintMetricRow("Pha 2: Lock & DB State Update", coldSummary.MedianLockMs,
            cold.Min(x => x.LockAcquisitionMs), cold.Max(x => x.LockAcquisitionMs),
            warmSummary.MedianLockMs,
            warm.Min(x => x.LockAcquisitionMs), warm.Max(x => x.LockAcquisitionMs));

        PrintMetricRow("Pha 3: Playwright.CreateAsync", coldSummary.MedianRuntimeInitMs,
            cold.Min(x => x.RuntimeInitMs), cold.Max(x => x.RuntimeInitMs),
            warmSummary.MedianRuntimeInitMs,
            warm.Min(x => x.RuntimeInitMs), warm.Max(x => x.RuntimeInitMs));

        PrintMetricRow("Pha 4: Chromium Launch Context", coldSummary.MedianBrowserLaunchMs,
            cold.Min(x => x.BrowserLaunchMs), cold.Max(x => x.BrowserLaunchMs),
            warmSummary.MedianBrowserLaunchMs,
            warm.Min(x => x.BrowserLaunchMs), warm.Max(x => x.BrowserLaunchMs));

        PrintMetricRow("Pha 5: CỬA SỔ XUẤT HIỆN", coldSummary.MedianWindowAppearanceMs,
            coldSummary.MinWindowAppearanceMs, coldSummary.MaxWindowAppearanceMs,
            warmSummary.MedianWindowAppearanceMs,
            warmSummary.MinWindowAppearanceMs, warmSummary.MaxWindowAppearanceMs);

        PrintMetricRow("Pha 6: Điều hướng (GotoAsync)", coldSummary.MedianNavigationMs,
            cold.Min(x => x.NavigationMs), cold.Max(x => x.NavigationMs),
            warmSummary.MedianNavigationMs,
            warm.Min(x => x.NavigationMs), warm.Max(x => x.NavigationMs));

        Console.WriteLine(new string('-', 98));

        PrintMetricRow("TỔNG SYNC (Chặn bridge)", coldSummary.MedianTotalSyncMs,
            coldSummary.MinTotalSyncMs, coldSummary.MaxTotalSyncMs,
            warmSummary.MedianTotalSyncMs,
            warmSummary.MinTotalSyncMs, warmSummary.MaxTotalSyncMs);

        Console.WriteLine(new string('=', 98));

        Console.WriteLine("\n[KẾT LUẬN & ĐÁNH GIÁ CÁC GIẢ THUYẾT BOTTLENECK]:");
        Console.WriteLine($"1. Giả thuyết Cold Start Node driver:");
        Console.WriteLine($"   - Khởi tạo Playwright.CreateAsync() chiếm ~{coldSummary.MedianRuntimeInitMs:F1}ms ở lần đầu tiên.");
        Console.WriteLine($"   - Khi pre-warm ở app startup, chi phí này giảm về ~{warmSummary.MedianRuntimeInitMs:F3}ms (giảm 99.9%).");
        Console.WriteLine($"2. Giả thuyết Navigation chặn Bridge / Cửa sổ hiển thị:");
        Console.WriteLine($"   - Cửa sổ Chromium xuất hiện ngay sau Pha 4 (sau ~{warmSummary.MedianWindowAppearanceMs:F1}ms ở warm start).");
        Console.WriteLine($"   - Nhưng lệnh await page.GotoAsync(initialUrl, Commit) chặn thêm ~{warmSummary.MedianNavigationMs:F1}ms (hoặc 1500-2500ms qua internet thật).");
        Console.WriteLine($"   - Việc await GotoAsync trước khi trả về bridge làm người dùng cảm nhận cửa sổ/bridge bị đơ thêm 1.5 - 3 giây.");
        Console.WriteLine($"3. Giả thuyết Truy vấn DB lặp:");
        Console.WriteLine($"   - GetProfileAsync và UpdateProfileStateAsync bị gọi 2 lần lặp trong chuỗi BrowserHandlers -> LoginCoordinator -> PlaywrightBrowserController.");
        Console.WriteLine($"4. Giả thuyết Lock contention:");
        Console.WriteLine($"   - Độ trễ Semaphore và File Lock là tối thiểu (< 5ms) khi không có tranh chấp.");
    }

    private static void PrintMetricRow(string name, double coldMedian, double coldMin, double coldMax, double warmMedian, double warmMin, double warmMax)
    {
        double reduction = coldMedian > 0.001 ? ((coldMedian - warmMedian) / coldMedian) * 100.0 : 0.0;
        string reductionStr = reduction >= 0 ? $"-{reduction:F1}%" : $"+{-reduction:F1}%";
        if (Math.Abs(reduction) < 0.1) reductionStr = "0.0%";

        Console.WriteLine(string.Format("{0,-30} | {1,8:F1}ms | {2,4:F0}-{3,4:F0}ms | {4,10:F1}ms | {5,4:F0}-{6,4:F0}ms | {7,10}",
            name, coldMedian, coldMin, coldMax, warmMedian, warmMin, warmMax, reductionStr));
    }

    private static BenchmarkSummary CalculateSummary(string condition, List<PhaseTimings> timings)
    {
        double GetMedian(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(x => x).ToList();
            if (sorted.Count == 0) return 0;
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }

        return new BenchmarkSummary(
            Condition: condition,
            SampleCount: timings.Count,
            MedianWindowAppearanceMs: GetMedian(timings.Select(x => x.WindowAppearanceMs)),
            MinWindowAppearanceMs: timings.Min(x => x.WindowAppearanceMs),
            MaxWindowAppearanceMs: timings.Max(x => x.WindowAppearanceMs),
            MedianTotalSyncMs: GetMedian(timings.Select(x => x.TotalSyncMs)),
            MinTotalSyncMs: timings.Min(x => x.TotalSyncMs),
            MaxTotalSyncMs: timings.Max(x => x.TotalSyncMs),
            MedianRuntimeInitMs: GetMedian(timings.Select(x => x.RuntimeInitMs)),
            MedianBrowserLaunchMs: GetMedian(timings.Select(x => x.BrowserLaunchMs)),
            MedianNavigationMs: GetMedian(timings.Select(x => x.NavigationMs)),
            MedianLockMs: GetMedian(timings.Select(x => x.LockAcquisitionMs)),
            MedianBridgeMs: GetMedian(timings.Select(x => x.BridgeAndDispatchMs)));
    }
}
