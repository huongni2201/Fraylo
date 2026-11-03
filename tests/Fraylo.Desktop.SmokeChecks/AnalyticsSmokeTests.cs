using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Analytics;
using Fraylo.Contracts.Analytics;
using Fraylo.Contracts.Bridge;
using Fraylo.Desktop.Bridge;
using Fraylo.Desktop.Bridge.Handlers;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói định lượng Task T25: TikTok Analytics & Affiliate Dashboard (P1).
/// Kiểm thử trực tiếp trên SQLite WAL thật, bao phủ:
/// - Test 1: Import CSV & JSON (Tiếng Việt, RFC 4180, bóc tách chính xác).
/// - Test 2: Cô lập dữ liệu theo TikTok account (không trộn lẫn số liệu).
/// - Test 3: Xử lý số liệu thiếu (Null Preservation - không bịa đặt số liệu, phân biệt null và 0).
/// - Test 4: Tính bất biến (Snapshot Immutability - đóng băng bằng chứng lịch sử).
/// - Test 5: Tổng hợp KPI, chuỗi thời gian (Timeseries) và Top Videos.
/// - Test 6: Xuất khẩu CSV (UTF-8 có BOM \uFEFF cho Excel).
/// - Test 7: Kiểm soát lỗi và xác thực đầu vào qua Bridge Dispatcher.
/// </summary>
public static class AnalyticsSmokeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class AnalyticsTestContext : IAsyncDisposable, IDisposable
    {
        public string TempDir { get; }
        public string DbPath { get; }
        public DbContextOptions<FrayloDbContext> DbOptions { get; }
        public AnalyticsService AnalyticsService { get; }
        public AnalyticsHandlers Handlers { get; }
        public BridgeDispatcher Dispatcher { get; }

        private AnalyticsTestContext(
            string tempDir,
            string dbPath,
            DbContextOptions<FrayloDbContext> dbOptions,
            AnalyticsService analyticsService,
            AnalyticsHandlers handlers,
            BridgeDispatcher dispatcher)
        {
            TempDir = tempDir;
            DbPath = dbPath;
            DbOptions = dbOptions;
            AnalyticsService = analyticsService;
            Handlers = handlers;
            Dispatcher = dispatcher;
        }

        public static async Task<AnalyticsTestContext> CreateAsync()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"Fraylo_analytics_smoke_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var dbPath = Path.Combine(tempDir, "analytics_smoke.db");
            var connectionString = DatabasePathHelper.BuildConnectionString(dbPath);

            var optionsBuilder = new DbContextOptionsBuilder<FrayloDbContext>();
            optionsBuilder.UseSqlite(connectionString)
                          .AddInterceptors(new SqlitePragmaInterceptor())
                          .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));

            var dbOptions = optionsBuilder.Options;

            // Khởi tạo schema cuối trên SQLite WAL thật.
            await using (var db = new FrayloDbContext(dbOptions))
            {
                await db.Database.EnsureCreatedAsync();
                var now = DateTime.UtcNow.ToString("o");
                db.BrowserProfiles.Add(new BrowserProfile
                {
                    Id = "acc-analytics-default",
                    Kind = "TikTok",
                    ProviderKey = "tiktok",
                    AccountIdentity = "@analytics-default",
                    SessionStatus = "Connected",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            var analyticsService = new AnalyticsService(dbOptions);
            var handlers = new AnalyticsHandlers(analyticsService);
            var dispatcher = new BridgeDispatcher();
            handlers.Register(dispatcher);

            return new AnalyticsTestContext(tempDir, dbPath, dbOptions, analyticsService, handlers, dispatcher);
        }

        public FrayloDbContext CreateDb() => new(DbOptions);

        public async Task SeedTikTokAccountAsync(string accountId)
        {
            await using var db = CreateDb();
            var now = DateTime.UtcNow.ToString("o");
            db.BrowserProfiles.Add(new BrowserProfile
            {
                Id = accountId,
                Kind = "TikTok",
                ProviderKey = "tiktok",
                AccountIdentity = $"@{accountId}",
                SessionStatus = "Connected",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
        }

        public void Dispose()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(TempDir))
                {
                    Directory.Delete(TempDir, recursive: true);
                }
            }
            catch
            {
                // Bỏ qua lỗi xóa thư mục tạm nếu file còn bị lock bởi OS
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static void Assert([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception($"[ASSERTION FAILED] {message}");
        }
    }

    private static RequestEnvelope CreateRequest(string command, object payload)
    {
        var id = Guid.NewGuid();
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        using var doc = JsonDocument.Parse(jsonBytes);
        return new RequestEnvelope(
            Id: id,
            Type: "request",
            Command: command,
            Payload: doc.RootElement.Clone(),
            ClientTimestampUtc: DateTimeOffset.UtcNow,
            ContractVersion: BridgeContract.Version
        );
    }

    private static T? DeserializeData<T>(ResponseEnvelope response)
    {
        Assert(response.Data.HasValue, "Response data element không được rỗng khi deserialize");
        return JsonSerializer.Deserialize<T>(response.Data.Value.GetRawText(), JsonOptions);
    }

    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("     Fraylo — Task T25: Analytics & Affiliate Dashboard Tests    ");
        Console.WriteLine("=================================================================");

        int passed = 0;
        int failed = 0;

        async Task RunCheckAsync(string name, Func<Task> test)
        {
            Console.Write($"[RUN] {name} ... ");
            try
            {
                await test();
                passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
            }
            catch (Exception ex)
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL");
                Console.WriteLine($"      Chi tiết lỗi: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"      Inner: {ex.InnerException.Message}");
                }
            }
            finally
            {
                Console.ResetColor();
            }
        }

        await RunCheckAsync("Test 1: Import CSV & JSON (Snapshot & Records)", Test_ImportCsvAndJson_Success);
        await RunCheckAsync("Test 2: Account isolation (cô lập 100% giữa các tài khoản)", Test_AccountIsolation_StrictSeparation);
        await RunCheckAsync("Test 3: Null Preservation (Bảo toàn số liệu thiếu, không fake 0)", Test_NullPreservation_NoFakeNumbers);
        await RunCheckAsync("Test 4: Snapshot Immutability (Đóng băng bằng chứng lịch sử)", Test_SnapshotImmutability_HistoricalFreezing);
        await RunCheckAsync("Test 5: Aggregation & Timeseries & Top Videos", Test_AggregationAndTimeseriesAndTopVideos);
        await RunCheckAsync("Test 6: Export CSV (UTF-8 có BOM chuẩn)", Test_ExportData_CsvUtf8WithBom);
        await RunCheckAsync("Test 7: Bridge Validation & Error Handling", Test_BridgeValidation_AndErrorHandling);
        await RunCheckAsync("Test 8: TikTok probe errors retain cause and selected profile", Test_TikTokProbeFailure_PreservesCause);
        await RunCheckAsync("Test 9: TikTok ZIP and quoted CSV exports", Test_TikTokZipCsvExports);

        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"Kết quả kiểm thử Analytics: {passed} PASSED, {failed} FAILED.");
        Console.WriteLine("=================================================================");

        return failed > 0 ? 1 : 0;
    }

    public static async Task Test_TikTokZipCsvExports()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"Fraylo_csv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string overview = "\"Date\",\"Video Views\",\"Profile Views\",\"Likes\",\"Comments\",\"Shares\"\r\n\"October 18\",\"0\",\"2\",\"3\",\"4\",\"5\"";
            const string content = "\"Time\",\"Video title\",\"Video link\",\"Post time\",\"Total likes\",\"Total comments\",\"Total shares\",\"Total views\"";
            const string viewers = "\"Date\",\"Total Viewers\",\"New Viewers\",\"Returning Viewers\"\r\n\"October 18\",\"undefined\",\"0\",\"1\"";
            var archivePath = Path.Combine(tempDir, "overview.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("Overview.csv").Open(), new System.Text.UTF8Encoding(true)))
                await writer.WriteAsync(overview);

            var method = typeof(TikTokAnalyticsSyncService).GetMethod("ReadExportCsvAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var csv = await (Task<string>)method.Invoke(null, new object[] { archivePath, "Overview", CancellationToken.None })!;
            var parser = new AnalyticsImportExportService();
            var parsed = parser.ParseTikTokStudioExports(csv, content, viewers, new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc));
            Assert(parsed.OverviewRecords.Count == 1 && parsed.OverviewRecords[0].VideoViews == 0 && parsed.OverviewRecords[0].ProfileViews == 2, "ZIP CSV phải giữ đúng số 0 và các metric.");
            Assert(parsed.ContentRecords.Count == 0 && parsed.ViewersRecords.Count == 1 && parsed.ViewersRecords[0].TotalViewers == null && parsed.ViewersRecords[0].NewViewers == 0, "CSV chỉ header và metric thiếu phải được xử lý đúng.");
            var bomParsed = parser.ParseTikTokStudioExports("\uFEFF" + overview, content, viewers, DateTime.UtcNow);
            Assert(bomParsed.OverviewRecords.Count == 1, "CSV có BOM phải được đọc đúng.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    public static async Task Test_TikTokProbeFailure_PreservesCause()
    {
        await using var ctx = await AnalyticsTestContext.CreateAsync();
        const string accountId = "analytics-tiktok-profile";
        const string profileId = accountId;
        const string probeError = "Không thể kiểm tra phiên TikTok: kết nối bị gián đoạn.";
        await using (var db = ctx.CreateDb())
        {
            db.BrowserProfiles.Add(new BrowserProfile
            {
                Id = profileId,
                Kind = "TikTok",
                ProviderKey = "tiktok",
                AccountIdentity = "@tiktok-test",
                SessionStatus = "Connected",
                CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                UpdatedAtUtc = DateTime.UtcNow.ToString("o")
            });
            await db.SaveChangesAsync();
        }

        var probe = new AnalyticsFailedSessionProbe(probeError);
        var login = new AnalyticsLoginRecorder();
        var crawler = new TikTokAnalyticsSyncService(ctx.DbOptions, new object(), new object(), login, probe);
        var session = await crawler.CheckSessionAsync(new TikTokCheckSessionRequest(null, AccountId: accountId));
        Assert(!session.IsLoggedIn && session.ErrorMessage == probeError, "Lỗi probe phải được giữ nguyên, không xác nhận phiên từ metadata.");
        Assert(probe.LastProfileId == profileId, "Probe phải dùng profile của account đã chọn.");

        var result = await crawler.SyncChannelDataAsync(new TikTokSyncChannelRequest(null, AccountId: accountId));
        Assert(result.Message.Contains(probeError, StringComparison.Ordinal), "Sync phải giữ nguyên lý do probe thất bại.");
        Assert(login.OpenCount == 0, "Lỗi probe không được tự mở/điều hướng cửa sổ đăng nhập.");
        Assert(!result.RequiresLogin, "Lỗi kiểm tra phiên chưa xác định không được kết luận yêu cầu đăng nhập.");
    }

    public sealed class AnalyticsFailedSessionProbe(string error)
    {
        public string? LastProfileId { get; private set; }
        public Task<Fraylo.Contracts.Accounts.ProfileIdentityResolutionResult> ResolveProfileIdentityAsync(
            string profileId, string provider, CancellationToken cancellationToken)
        {
            LastProfileId = profileId;
            return Task.FromResult(new Fraylo.Contracts.Accounts.ProfileIdentityResolutionResult(
                false, profileId, provider, null, "Unverified", error));
        }
    }

    public sealed class AnalyticsLoginRecorder
    {
        public int OpenCount { get; private set; }
        public Task<Fraylo.Contracts.Browser.BrowserRequestLoginResponseData> RequestLoginAsync(
            string profileId, string targetUrl, CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult(new Fraylo.Contracts.Browser.BrowserRequestLoginResponseData(false, "VisibleActive", profileId));
        }
    }

    /// <summary>
    /// Test 1: Nhập khẩu CSV (tiếng Việt UTF-8) và JSON mảng, xác minh Snapshot và Records được lưu vào DB.
    /// </summary>
    public static async Task Test_ImportCsvAndJson_Success()
    {
        await using var ctx = await AnalyticsTestContext.CreateAsync();

        // 1. Import CSV
        string csv = """
        video_key,title,views,likes,comments,shares,watch_time_avg_sec,gmv_amount,affiliate_clicks,published_at_utc
        v_001,"Cách làm Affiliate TikTok triệu view 🚀",150000,12000,850,420,18.5,2500000,120,2026-10-20T10:00:00Z
        v_002,"Top 5 sản phẩm bán chạy nhất tháng 10 💥",85000,6200,310,150,14.2,1800000,95,2026-10-21T14:30:00Z
        v_003,"Hướng dẫn quay video ngắn bán hàng 🎬",45000,3100,180,80,12.0,900000,45,2026-10-22T08:15:00Z
        """;

        var importCsvReq = CreateRequest("analytics.import_data", new
        {
            accountId = "acc-analytics-default",
            format = "csv",
            rawContent = csv,
            source = "manual_csv"
        });

        var respCsv = await ctx.Dispatcher.DispatchAsync(importCsvReq, CancellationToken.None);
        Assert(respCsv.Success, $"Import CSV phải thành công: {respCsv.Error?.Message}");

        var csvResult = DeserializeData<AnalyticsImportResultDto>(respCsv);
        Assert(csvResult != null, "Kết quả import CSV không được null");
        Assert(csvResult!.Success, "ImportResult.Success phải là true");
        Assert(csvResult.RecordCount == 3, $"RecordCount phải bằng 3, thực tế: {csvResult.RecordCount}");
        Assert(!string.IsNullOrWhiteSpace(csvResult.SnapshotId), "SnapshotId không được rỗng");

        // Xác minh trực tiếp trong SQLite DB
        await using (var db = ctx.CreateDb())
        {
            var snapshot = await db.Set<AnalyticsSnapshot>().FirstOrDefaultAsync(s => s.Id == csvResult.SnapshotId);
            Assert(snapshot != null, "Snapshot CSV phải được lưu vào DB");
            Assert(snapshot!.RecordCount == 3, "Snapshot.RecordCount trong DB phải là 3");
            Assert(snapshot.AccountId == "acc-analytics-default", "Snapshot phải thuộc tài khoản đã chọn.");
            Assert(snapshot.Source == "manual_csv", "Snapshot.Source trong DB phải đúng");

            var records = await db.Set<VideoAnalyticsRecord>()
                .Where(r => r.SnapshotId == csvResult.SnapshotId)
                .ToListAsync();

            Assert(records.Count == 3, $"DB phải có 3 records thuộc snapshot, thực tế: {records.Count}");
            var r1 = records.FirstOrDefault(r => r.VideoKey == "v_001");
            Assert(r1 != null, "Phải tìm thấy video v_001");
            Assert(r1!.Title == "Cách làm Affiliate TikTok triệu view 🚀", "Tiêu đề tiếng Việt phải giữ nguyên");
            Assert(r1.Views == 150000, "Views phải đúng 150000");
            Assert(r1.GmvAmount == 2500000m, "GMV Amount phải đúng 2500000");
        }

        // 2. Import JSON
        string json = """
        [
          {
            "video_key": "v_json_01",
            "title": "Review nồi chiên không dầu Philips",
            "views": 20000,
            "likes": 1500,
            "comments": 90,
            "shares": 30,
            "watch_time_avg_sec": 15.0,
            "gmv_amount": 500000,
            "affiliate_clicks": 25,
            "published_at_utc": "2026-10-23T09:00:00Z"
          },
          {
            "video_key": "v_json_02",
            "title": "Đập hộp robot hút bụi thông minh",
            "views": 35000,
            "likes": 2800,
            "comments": 140,
            "shares": 65,
            "watch_time_avg_sec": 19.5,
            "gmv_amount": 1200000,
            "affiliate_clicks": 60,
            "published_at_utc": "2026-10-24T11:00:00Z"
          }
        ]
        """;

        var importJsonReq = CreateRequest("analytics.import_data", new
        {
            accountId = "acc-analytics-default",
            format = "json",
            rawContent = json,
            source = "manual_json"
        });

        var respJson = await ctx.Dispatcher.DispatchAsync(importJsonReq, CancellationToken.None);
        Assert(respJson.Success, $"Import JSON phải thành công: {respJson.Error?.Message}");

        var jsonResult = DeserializeData<AnalyticsImportResultDto>(respJson);
        Assert(jsonResult != null && jsonResult.RecordCount == 2, "Import JSON phải tạo đúng 2 records");

        await using (var db = ctx.CreateDb())
        {
            var count = await db.Set<VideoAnalyticsRecord>().CountAsync(r => r.SnapshotId == jsonResult!.SnapshotId);
            Assert(count == 2, "DB phải có đúng 2 bản ghi từ JSON");
        }
    }

    /// <summary>
    /// Test 2: Cô lập account - dữ liệu giữa TikTok account A và B không được trộn lẫn.
    /// </summary>
    public static async Task Test_AccountIsolation_StrictSeparation()
    {
        await using var ctx = await AnalyticsTestContext.CreateAsync();
        await ctx.SeedTikTokAccountAsync("acc-alpha");
        await ctx.SeedTikTokAccountAsync("acc-beta");

        // Nạp dữ liệu account A (Tổng views: 100,000).
        string csvAlpha = """
        video_key,title,views,likes,comments,shares,watch_time_avg_sec,gmv_amount,affiliate_clicks,published_at_utc
        v_alpha_1,"Alpha Video 1",40000,2000,100,50,15.0,1000000,50,2026-10-20T10:00:00Z
        v_alpha_2,"Alpha Video 2",60000,3000,200,80,18.0,1500000,75,2026-10-21T10:00:00Z
        """;

        // Nạp dữ liệu account B (Tổng views: 500,000).
        string csvBeta = """
        video_key,title,views,likes,comments,shares,watch_time_avg_sec,gmv_amount,affiliate_clicks,published_at_utc
        v_beta_1,"Beta Video 1",300000,15000,800,400,20.0,8000000,300,2026-10-20T10:00:00Z
        v_beta_2,"Beta Video 2",200000,10000,500,250,16.0,5000000,200,2026-10-21T10:00:00Z
        """;

        await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.import_data", new
        {
            accountId = "acc-alpha",
            format = "csv",
            rawContent = csvAlpha
        }), CancellationToken.None);

        await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.import_data", new
        {
            accountId = "acc-beta",
            format = "csv",
            rawContent = csvBeta
        }), CancellationToken.None);

        // Truy vấn summary của account A.
        var summaryAlphaReq = CreateRequest("analytics.get_summary", new { accountId = "acc-alpha" });
        var summaryAlphaResp = await ctx.Dispatcher.DispatchAsync(summaryAlphaReq, CancellationToken.None);
        Assert(summaryAlphaResp.Success, "Get summary alpha phải thành công");

        var summaryAlpha = DeserializeData<AnalyticsSummaryDto>(summaryAlphaResp);
        Assert(summaryAlpha != null, "Summary alpha không được null");
        Assert(summaryAlpha!.TotalVideos == 2, $"Alpha phải có 2 videos, thực tế: {summaryAlpha.TotalVideos}");
        Assert(summaryAlpha.TotalViews == 100000, $"Alpha views phải là 100,000, thực tế: {summaryAlpha.TotalViews}");
        Assert(summaryAlpha.TotalLikes == 5000, $"Alpha likes phải là 5,000, thực tế: {summaryAlpha.TotalLikes}");
        Assert(summaryAlpha.TotalGmvAmount == 2500000m, "Alpha GMV phải đúng 2,500,000");

        // Truy vấn top videos của account A.
        var topVideosAlphaReq = CreateRequest("analytics.list_top_videos", new { accountId = "acc-alpha" });
        var topVideosAlphaResp = await ctx.Dispatcher.DispatchAsync(topVideosAlphaReq, CancellationToken.None);
        Assert(topVideosAlphaResp.Success, "List top videos alpha phải thành công");

        using var doc = JsonDocument.Parse(topVideosAlphaResp.Data!.Value.GetRawText());
        var videosElement = doc.RootElement.GetProperty("videos");
        var alphaVideos = JsonSerializer.Deserialize<List<VideoMetricRecordDto>>(videosElement.GetRawText(), JsonOptions);

        Assert(alphaVideos != null && alphaVideos.Count == 2, "Alpha chỉ được có đúng 2 videos");
        foreach (var v in alphaVideos!)
        {
            Assert(v.AccountId == "acc-alpha", $"AccountId phải là acc-alpha, thực tế: {v.AccountId}");
            Assert(!v.VideoKey.StartsWith("v_beta", StringComparison.OrdinalIgnoreCase),
                $"Tuyệt đối không được rò rỉ video của beta vào alpha: {v.VideoKey}");
        }
    }

    /// <summary>
    /// Test 3: Xử lý số liệu thiếu (Null Preservation) - Giá trị khuyết phải giữ nguyên null, tuyệt đối không bịa đặt 0.
    /// </summary>
    public static async Task Test_NullPreservation_NoFakeNumbers()
    {
        await using var ctx = await AnalyticsTestContext.CreateAsync();

        string csvSparse = """
        video_key,title,views,likes,comments,shares,watch_time_avg_sec,gmv_amount,affiliate_clicks,published_at_utc
        v_sparse_1,"Video không công bố views",,100,,20,,,,"2026-10-25T12:00:00Z"
        v_sparse_2,"Video không có affiliate",5000,200,10,5,12.5,,,"2026-10-25T13:00:00Z"
        """;

        var importResp = await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.import_data", new
        {
            accountId = "acc-analytics-default",
            format = "csv",
            rawContent = csvSparse
        }), CancellationToken.None);
        Assert(importResp.Success, "Import csv sparse phải thành công");

        // Kiểm tra Top Videos
        var topResp = await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.list_top_videos", new
        {
            accountId = "acc-analytics-default"
        }), CancellationToken.None);
        Assert(topResp.Success, "List top videos sparse phải thành công");

        using var doc = JsonDocument.Parse(topResp.Data!.Value.GetRawText());
        var videos = JsonSerializer.Deserialize<List<VideoMetricRecordDto>>(doc.RootElement.GetProperty("videos").GetRawText(), JsonOptions);
        Assert(videos != null && videos.Count == 2, "Phải có 2 videos");

        var v1 = videos.FirstOrDefault(v => v.VideoKey == "v_sparse_1");
        Assert(v1 != null, "Phải có v_sparse_1");
        Assert(v1!.Views == null, "v_sparse_1: Views khuyết BẮT BUỘC là null, KHÔNG ĐƯỢC là 0");
        Assert(v1.Comments == null, "v_sparse_1: Comments khuyết BẮT BUỘC là null, KHÔNG ĐƯỢC là 0");
        Assert(v1.GmvAmount == null, "v_sparse_1: GmvAmount khuyết BẮT BUỘC là null");
        Assert(v1.AffiliateClicks == null, "v_sparse_1: AffiliateClicks khuyết BẮT BUỘC là null");
        Assert(v1.WatchTimeAvgSec == null, "v_sparse_1: WatchTimeAvgSec khuyết BẮT BUỘC là null");

        var v2 = videos.FirstOrDefault(v => v.VideoKey == "v_sparse_2");
        Assert(v2 != null, "Phải có v_sparse_2");
        Assert(v2!.Views == 5000, "v_sparse_2: Views có số liệu phải là 5000");
        Assert(v2.GmvAmount == null, "v_sparse_2: GmvAmount khuyết BẮT BUỘC là null");
        Assert(v2.AffiliateClicks == null, "v_sparse_2: AffiliateClicks khuyết BẮT BUỘC là null");

        // Kiểm tra Summary khi không có doanh thu GMV
        var summaryResp = await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.get_summary", new
        {
            accountId = "acc-analytics-default"
        }), CancellationToken.None);
        var summary = DeserializeData<AnalyticsSummaryDto>(summaryResp);

        Assert(summary != null, "Summary không được null");
        Assert(summary!.TotalGmvAmount == null, "Khi toàn bộ bản ghi không có GMV, TotalGmvAmount BẮT BUỘC là null");
        Assert(summary.Currency == null, "Khi không có GMV, Currency BẮT BUỘC là null");
    }

    /// <summary>
    /// Test 4: Tính bất biến (Snapshot Immutability) - Mỗi lần nhập là một snapshot đóng băng lịch sử, không ghi đè snapshot cũ.
    /// </summary>
    public static async Task Test_SnapshotImmutability_HistoricalFreezing()
    {
        await using var ctx = await AnalyticsTestContext.CreateAsync();

        // Snapshot đợt 1
        string batch1 = """
        video_key,title,views,likes,comments,shares,watch_time_avg_sec,gmv_amount,affiliate_clicks,published_at_utc
        v_fr_01,"Video bất biến 1",1000,50,5,2,10.0,100000,5,2026-10-20T10:00:00Z
        v_fr_02,"Video bất biến 2",2000,100,10,4,12.0,200000,10,2026-10-21T10:00:00Z
        """;

        var resp1 = await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.import_data", new
        {
            accountId = "acc-analytics-default",
            format = "csv",
            rawContent = batch1
        }), CancellationToken.None);

        var res1 = DeserializeData<AnalyticsImportResultDto>(resp1);
        Assert(res1 != null && !string.IsNullOrWhiteSpace(res1.SnapshotId), "Import đợt 1 phải trả về SnapshotId");
        string snapshot1Id = res1!.SnapshotId;

        // Chờ 20ms đảm bảo khoảng cách thời gian
        await Task.Delay(20);

        // Snapshot đợt 2 (Cùng video_key nhưng số liệu lượt xem tăng lên)
        string batch2 = """
        video_key,title,views,likes,comments,shares,watch_time_avg_sec,gmv_amount,affiliate_clicks,published_at_utc
        v_fr_01,"Video bất biến 1",5000,250,25,10,10.0,500000,25,2026-10-20T10:00:00Z
        v_fr_02,"Video bất biến 2",8000,400,40,16,12.0,800000,40,2026-10-21T10:00:00Z
        v_fr_03,"Video mới xuất hiện",3000,150,15,6,15.0,300000,15,2026-10-22T10:00:00Z
        """;

        var resp2 = await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.import_data", new
        {
            accountId = "acc-analytics-default",
            format = "csv",
            rawContent = batch2
        }), CancellationToken.None);

        var res2 = DeserializeData<AnalyticsImportResultDto>(resp2);
        Assert(res2 != null && !string.IsNullOrWhiteSpace(res2.SnapshotId), "Import đợt 2 phải trả về SnapshotId");
        string snapshot2Id = res2!.SnapshotId;

        Assert(snapshot1Id != snapshot2Id, "Snapshot 2 phải có Id mới hoàn toàn, không được ghi đè Id cũ");

        // Xác minh toàn vẹn trong DB: Snapshot 1 vẫn tồn tại nguyên vẹn 100%
        await using (var db = ctx.CreateDb())
        {
            var s1 = await db.Set<AnalyticsSnapshot>().FirstOrDefaultAsync(s => s.Id == snapshot1Id);
            Assert(s1 != null, "Snapshot 1 vẫn phải tồn tại trong DB");
            Assert(s1!.RecordCount == 2, "Snapshot 1 record count vẫn phải là 2");

            var s1Records = await db.Set<VideoAnalyticsRecord>().Where(r => r.SnapshotId == snapshot1Id).ToListAsync();
            Assert(s1Records.Count == 2, "Snapshot 1 vẫn có đủ 2 records");
            var v1InSnap1 = s1Records.FirstOrDefault(r => r.VideoKey == "v_fr_01");
            Assert(v1InSnap1 != null && v1InSnap1.Views == 1000, "Views trong Snapshot 1 phải đóng băng ở 1000, không bị update thành 5000");

            var s2 = await db.Set<AnalyticsSnapshot>().FirstOrDefaultAsync(s => s.Id == snapshot2Id);
            Assert(s2 != null, "Snapshot 2 phải tồn tại trong DB");
            Assert(s2!.RecordCount == 3, "Snapshot 2 record count phải là 3");

            var s2Records = await db.Set<VideoAnalyticsRecord>().Where(r => r.SnapshotId == snapshot2Id).ToListAsync();
            Assert(s2Records.Count == 3, "Snapshot 2 có đủ 3 records");
            var v1InSnap2 = s2Records.FirstOrDefault(r => r.VideoKey == "v_fr_01");
            Assert(v1InSnap2 != null && v1InSnap2.Views == 5000, "Views trong Snapshot 2 là 5000");
        }
    }

    /// <summary>
    /// Test 5: Tổng hợp KPI, tính tỷ lệ tương tác (Engagement Rate), Timeseries và sắp xếp Top Videos.
    /// </summary>
    public static async Task Test_AggregationAndTimeseriesAndTopVideos()
    {
        await using var ctx = await AnalyticsTestContext.CreateAsync();

        string csv = """
        video_key,title,views,likes,comments,shares,watch_time_avg_sec,gmv_amount,affiliate_clicks,published_at_utc
        v_agg_01,"Video 1",10000,600,200,100,15.0,1000000,50,2026-10-20T10:00:00Z
        v_agg_02,"Video 2",30000,1800,600,300,18.0,3000000,150,2026-10-21T10:00:00Z
        """;

        await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.import_data", new
        {
            accountId = "acc-analytics-default",
            format = "csv",
            rawContent = csv
        }), CancellationToken.None);

        // 1. Kiểm tra Summary:
        // Tổng views = 40,000
        // Tổng tương tác = (600 + 1800) + (200 + 600) + (100 + 300) = 2400 + 800 + 400 = 3600
        // Engagement rate = 3600 / 40000 * 100% = 9.0%
        var summaryResp = await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.get_summary", new
        {
            accountId = "acc-analytics-default"
        }), CancellationToken.None);
        Assert(summaryResp.Success, "Get summary agg phải thành công");

        var summary = DeserializeData<AnalyticsSummaryDto>(summaryResp);
        Assert(summary != null, "Summary không được null");
        Assert(summary!.TotalVideos == 2, "TotalVideos phải là 2");
        Assert(summary.TotalViews == 40000, $"TotalViews phải là 40,000, thực tế: {summary.TotalViews}");
        Assert(summary.TotalLikes == 2400, $"TotalLikes phải là 2,400, thực tế: {summary.TotalLikes}");
        Assert(summary.TotalComments == 800, $"TotalComments phải là 800, thực tế: {summary.TotalComments}");
        Assert(summary.TotalShares == 400, $"TotalShares phải là 400, thực tế: {summary.TotalShares}");
        Assert(summary.TotalGmvAmount == 4000000m, "TotalGmvAmount phải là 4,000,000");
        Assert(summary.Currency == "VND", "Currency phải là VND khi có GMV");
        Assert(summary.AverageEngagementRate.HasValue, "AverageEngagementRate phải có giá trị");
        Assert(Math.Abs(summary.AverageEngagementRate!.Value - 9.0) < 0.05,
            $"AverageEngagementRate phải xấp xỉ 9.0%, thực tế: {summary.AverageEngagementRate}");

        // 2. Kiểm tra Timeseries theo views
        var timeseriesResp = await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.get_timeseries", new
        {
            accountId = "acc-analytics-default",
            metric = "views"
        }), CancellationToken.None);
        Assert(timeseriesResp.Success, "Get timeseries views phải thành công");

        using var tsDoc = JsonDocument.Parse(timeseriesResp.Data!.Value.GetRawText());
        var pointsElement = tsDoc.RootElement.GetProperty("points");
        var points = JsonSerializer.Deserialize<List<AnalyticsTimeseriesPointDto>>(pointsElement.GetRawText(), JsonOptions);
        Assert(points != null && points.Count > 0, "Timeseries points phải có ít nhất 1 điểm dữ liệu");

        // 3. Kiểm tra Top Videos sắp xếp theo views giảm dần
        var topVideosResp = await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.list_top_videos", new
        {
            accountId = "acc-analytics-default",
            sortBy = "views",
            limit = 10
        }), CancellationToken.None);
        Assert(topVideosResp.Success, "List top videos phải thành công");

        using var topDoc = JsonDocument.Parse(topVideosResp.Data!.Value.GetRawText());
        var videos = JsonSerializer.Deserialize<List<VideoMetricRecordDto>>(topDoc.RootElement.GetProperty("videos").GetRawText(), JsonOptions);
        Assert(videos != null && videos.Count == 2, "Top videos phải trả về 2 video");
        Assert(videos![0].VideoKey == "v_agg_02", "Video 2 (30,000 views) phải xếp trên Video 1 (10,000 views)");
        Assert(videos[1].VideoKey == "v_agg_01", "Video 1 (10,000 views) phải xếp thứ hai");
    }

    /// <summary>
    /// Test 6: Xuất khẩu CSV có BOM UTF-8 (\uFEFF) cho Excel, header chuẩn, dữ liệu toàn vẹn.
    /// </summary>
    public static async Task Test_ExportData_CsvUtf8WithBom()
    {
        await using var ctx = await AnalyticsTestContext.CreateAsync();

        string csv = """
        video_key,title,views,likes,comments,shares,watch_time_avg_sec,gmv_amount,affiliate_clicks,published_at_utc
        v_exp_01,"Video xuất khẩu mẫu 🎯",50000,2500,120,60,16.5,1500000,80,2026-10-22T10:00:00Z
        """;

        await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.import_data", new
        {
            accountId = "acc-analytics-default",
            format = "csv",
            rawContent = csv
        }), CancellationToken.None);

        var exportResp = await ctx.Dispatcher.DispatchAsync(CreateRequest("analytics.export_data", new
        {
            accountId = "acc-analytics-default",
            format = "csv"
        }), CancellationToken.None);

        Assert(exportResp.Success, $"Export data phải thành công: {exportResp.Error?.Message}");
        var exportResult = DeserializeData<AnalyticsExportResultDto>(exportResp);
        Assert(exportResult != null, "Export result không được null");
        Assert(exportResult!.Format == "csv", "Format phải là csv");
        Assert(exportResult.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), "FileName phải có đuôi .csv");

        // Kiểm tra BOM UTF-8 (\uFEFF)
        string content = exportResult.Content;
        Assert(content.Length > 0, "Nội dung export không được rỗng");
        Assert(content[0] == '\uFEFF', "Nội dung export BẮT BUỘC bắt đầu bằng ký tự BOM '\\uFEFF' để Excel hiển thị đúng tiếng Việt");

        // Kiểm tra header và dữ liệu
        Assert(content.Contains("video_key,title,views", StringComparison.OrdinalIgnoreCase), "CSV phải chứa header hợp lệ");
        Assert(content.Contains("v_exp_01", StringComparison.Ordinal), "CSV phải chứa video_key v_exp_01");
        Assert(content.Contains("Video xuất khẩu mẫu 🎯", StringComparison.Ordinal), "CSV phải giữ nguyên Unicode tiếng Việt có emoji");
    }

    /// <summary>
    /// Test 7: Kiểm soát ngoại lệ và lỗi validation đầu vào của Bridge Dispatcher.
    /// </summary>
    public static async Task Test_BridgeValidation_AndErrorHandling()
    {
        await using var ctx = await AnalyticsTestContext.CreateAsync();

        // 1. import_data thiếu accountId
        var invalidImport1 = CreateRequest("analytics.import_data", new
        {
            format = "csv",
            rawContent = "video_key,title\nv1,t1"
        });
        var resp1 = await ctx.Dispatcher.DispatchAsync(invalidImport1, CancellationToken.None);
        Assert(!resp1.Success, "Import thiếu accountId phải thất bại");
        Assert(resp1.Error?.Code == "INVALID_ARGUMENT", $"Error code phải là INVALID_ARGUMENT, thực tế: {resp1.Error?.Code}");

        // 2. import_data thiếu rawContent
        var invalidImport2 = CreateRequest("analytics.import_data", new
        {
            accountId = "acc-analytics-default",
            format = "csv",
            rawContent = ""
        });
        var resp2 = await ctx.Dispatcher.DispatchAsync(invalidImport2, CancellationToken.None);
        Assert(!resp2.Success, "Import thiếu rawContent phải thất bại");
        Assert(resp2.Error?.Code == "INVALID_ARGUMENT", "Error code phải là INVALID_ARGUMENT");

        // 3. get_summary thiếu accountId
        var invalidSummary = CreateRequest("analytics.get_summary", new { });
        var resp3 = await ctx.Dispatcher.DispatchAsync(invalidSummary, CancellationToken.None);
        Assert(!resp3.Success, "Summary thiếu accountId phải thất bại");
        Assert(resp3.Error?.Code == "INVALID_ARGUMENT", "Error code phải là INVALID_ARGUMENT");

        // 4. get_timeseries thiếu metric
        var invalidTs = CreateRequest("analytics.get_timeseries", new { accountId = "acc-test" });
        var resp4 = await ctx.Dispatcher.DispatchAsync(invalidTs, CancellationToken.None);
        Assert(!resp4.Success, "Timeseries thiếu metric phải thất bại");
        Assert(resp4.Error?.Code == "INVALID_ARGUMENT", "Error code phải là INVALID_ARGUMENT");

        // 5. Unregister và kiểm tra UNKNOWN_COMMAND
        ctx.Handlers.Unregister(ctx.Dispatcher);
        var unregReq = CreateRequest("analytics.get_summary", new { accountId = "acc-test" });
        var resp5 = await ctx.Dispatcher.DispatchAsync(unregReq, CancellationToken.None);
        Assert(!resp5.Success, "Sau khi unregister, command phải thất bại");
    }
}
