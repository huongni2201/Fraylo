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
        await ValidateCapturedResponseAsync();
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
            Check(completed.GetProperty("result").GetProperty("limitations").GetArrayLength() == 11,
                "Eleven limitations survive SQLite persistence and bridge parsing.");
            var input = completed.GetProperty("input");
            Check(input.GetProperty("totalRecords").GetInt32() == 75 && input.GetProperty("omittedRecords").GetInt32() > 0 &&
                input.GetProperty("totals").EnumerateArray().Any(t => t.GetProperty("metric").GetString() == "views" &&
                    t.GetProperty("value").GetInt32() == 2850), "Totals use all rows before sampling.");
            Check(input.GetProperty("sample").EnumerateArray().Any(r => r.GetProperty("metrics").GetProperty("likes").GetProperty("value").GetInt32() == 0),
                "Zero survives normalization.");
            Check(adapter.Prompt is { Length: <= 8000 } && !adapter.Prompt.Contains("token=removed") &&
                !adapter.Prompt.Contains("test@example.invalid"), "Payload is bounded and excludes links and account identity.");
            Check(adapter.Prompt!.Contains("limitations từ 1–16 mục"), "Prompt declares the accepted limitation count.");
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

    private static async Task ValidateCapturedResponseAsync()
    {
        var registry = new ProviderRegistry();
        var adapter = new FixtureAdapter { Response = CapturedResponse };
        registry.RegisterAdapter(adapter);
        var executor = new ProductionJobExecutor(registry);
        // Isolate output validation with known IDs; the original source snapshot is not part of the captured response.
        var input = JsonSerializer.Serialize(new
        {
            version = 1, platform = "facebook", source = "Meta Business Suite", scope = "Lifetime",
            totals = Enumerable.Range(1, 6).Select(i => new { id = $"T{i}" }),
            sample = Enumerable.Range(1, 6).Select(i => new { id = $"R{i}", metrics = new { reactions = new { value = 0 } } })
        });
        var context = new JobExecutionContext("captured-analysis", null, null, JobTaskType.AnalyticsAnalysis,
            ProviderKeys.ChatGPTWeb, "fixture", "{\"renderedText\":\"fixture\"}", input, 1, 1);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var captured = JsonSerializer.Deserialize<AnalyticsAnalysisResult>(CapturedResponse, json)!;
        adapter.Response = JsonSerializer.Serialize(captured with { Limitations = captured.Limitations.Take(8).ToArray() }, json);
        Check((await executor.ExecuteAsync(context, () => Task.CompletedTask, null, default)).Success,
            "The same response with only eight limitations is accepted.");
        adapter.Response = CapturedResponse;
        var result = await executor.ExecuteAsync(context, () => Task.CompletedTask, null, default);
        Check(result.Success && result.ResultText == CapturedResponse,
            "Captured Facebook response with eleven limitations is accepted without losing text: " + result.ReasonCode);
        var atLimit = captured with { Limitations = Enumerable.Repeat("Giới hạn dữ liệu", 16).ToArray() };
        adapter.Response = JsonSerializer.Serialize(atLimit, json);
        Check((await executor.ExecuteAsync(context, () => Task.CompletedTask, null, default)).Success,
            "Sixteen limitations are accepted.");
        foreach (var invalid in new[]
        {
            captured with { Limitations = Enumerable.Repeat("Giới hạn dữ liệu", 17).ToArray() },
            captured with { Limitations = [] },
            captured with { Limitations = ["https://example.invalid"] },
            captured with { SchemaVersion = 2 },
            captured with { Highlights = [new("Căn cứ không có trong nguồn", ["not-in-snapshot"])] },
            captured with { Summary = "Có 7 bản ghi" }
        })
        {
            adapter.Response = JsonSerializer.Serialize(invalid, json);
            var rejected = await executor.ExecuteAsync(context, () => Task.CompletedTask, null, default);
            Check(!rejected.Success && rejected.ReasonCode == "InvalidAnalysisResult" && rejected.ResultText == adapter.Response,
                "Invalid count, URL, schema or evidence remains rejected; raw response is retained.");
        }
        adapter.Response = CapturedResponse[..^1];
        var truncated = await executor.ExecuteAsync(context, () => Task.CompletedTask, null, default);
        Check(!truncated.Success && truncated.ReasonCode == "InvalidAnalysisResult", "Truncated JSON remains rejected.");
    }

    private const string CapturedResponse = """
        {
        "schemaVersion": 1,
        "summary": "Dữ liệu Facebook từ Meta Business Suite cho thấy cả Reel và bài viết đều ghi nhận lượt cảm xúc, nhưng chưa ghi nhận bình luận hoặc chia sẻ. Hai Reel có thời gian xem trung bình ngắn. Chưa có dữ liệu lượt xem để đánh giá khả năng tiếp cận hoặc tỷ lệ tương tác. Bộ dữ liệu bị rút gọn nên chưa đủ căn cứ kết luận hiệu quả tổng thể hay xu hướng tăng trưởng.",
        "highlights": [
        {"text": "Nhóm Reel đã ghi nhận lượt cảm xúc trên tất cả nội dung được thống kê.", "evidence": ["T1"]},
        {"text": "Nhóm bài viết có lượt cảm xúc, cho thấy một phần nội dung nhận được phản hồi từ người xem.", "evidence": ["T4"]}
        ],
        "improvements": [
        {"text": "Nhóm Reel chưa ghi nhận bình luận hoặc chia sẻ, cần thử cách khuyến khích người xem trao đổi và chia sẻ nội dung.", "evidence": ["T2", "T3"]},
        {"text": "Nhóm bài viết cũng chưa ghi nhận bình luận hoặc chia sẻ, cần cải thiện cách đặt câu hỏi và truyền tải thông tin hữu ích.", "evidence": ["T5", "T6"]},
        {"text": "Các Reel trong mẫu có thời gian xem trung bình ngắn, nên thử cách mở đầu trực tiếp và trình bày nội dung súc tích hơn.", "evidence": ["R1", "R4"]},
        {"text": "Một số bài viết về dịch vụ cho thuê nhà vệ sinh chưa ghi nhận cảm xúc, bình luận hoặc chia sẻ. Cần thử cách trình bày khác trước khi đánh giá sức hấp dẫn của chủ đề.", "evidence": ["R3", "R6"]}
        ],
        "recommendations": [
        {"text": "Thử 2 phiên bản mở đầu Reel: một phiên bản nêu trực tiếp vấn đề khách hàng gặp phải, một phiên bản giới thiệu giải pháp ngay từ đầu. Giữ nội dung chính tương đương để so sánh khả năng giữ chân. Theo dõi thời gian xem trung bình tính bằng giây, lượt xem và tỷ lệ giữ chân khi có dữ liệu.", "evidence": ["R1", "R4"]},
        {"text": "Thử thêm câu hỏi cụ thể ở cuối bài viết, chẳng hạn khách hàng thường gặp vấn đề tắc nghẽn ở vị trí nào. Mục tiêu là tạo cơ hội trao đổi thay vì chỉ giới thiệu dịch vụ. Theo dõi số bình luận trên từng bài viết.", "evidence": ["T5", "R2", "R5"]},
        {"text": "Thử Reel hướng dẫn nhận biết dấu hiệu tắc cống hoặc thời điểm cần kiểm tra bể phốt, kết hợp lời mời chia sẻ thông tin hữu ích. Mục tiêu là kiểm tra khả năng tạo tương tác ngoài lượt cảm xúc. Theo dõi lượt chia sẻ, bình luận và cảm xúc trên từng Reel.", "evidence": ["T1", "T2", "T3", "R1"]},
        {"text": "Thử 2 cách trình bày bài viết về dịch vụ nhà vệ sinh di động: một bài tập trung vào hình ảnh sử dụng thực tế, một bài giải thích các tình huống cần thuê. Mục tiêu là đánh giá cách truyền tải phù hợp hơn với chủ đề. Theo dõi lượt cảm xúc, bình luận, chia sẻ và lượt xem nếu được cung cấp.", "evidence": ["R3", "R6"]},
        {"text": "Thử đăng các nội dung cùng chủ đề theo lịch ổn định, phân nhóm Reel và bài viết để đánh giá riêng. Mục tiêu là tạo thêm dữ liệu có thể so sánh thay vì kết luận từ mẫu hiện tại. Theo dõi lượt xem, cảm xúc, bình luận, chia sẻ và thời gian xem trung bình đối với Reel.", "evidence": ["T1", "T4", "R1", "R4"]}
        ],
        "limitations": [
        "Dữ liệu chỉ có 7 bản ghi, trong đó 6 bản ghi được đưa vào mẫu và 1 bản ghi bị lược bỏ. Các nhận xét về từng nội dung chỉ áp dụng cho mẫu được cung cấp.",
        "Mẫu được lựa chọn theo lượt xem cao hoặc thấp, ngày đăng mới và tình trạng thiếu chỉ số nên không đại diện ngẫu nhiên cho toàn bộ nội dung.",
        "Các tổng phản ánh 2 Reel và 5 bài viết có dữ liệu tương ứng, không phải toàn bộ hoạt động của trang Facebook.",
        "Không có ngày bắt đầu và kết thúc kỳ báo cáo. Phạm vi nguồn được mô tả là trọn đời và tối đa 50 nội dung.",
        "Tất cả bản ghi mẫu đều thiếu lượt xem. Không thể tính tỷ lệ tương tác theo lượt xem, so sánh hiệu quả tiếp cận hoặc đánh giá tỷ lệ chuyển đổi.",
        "Không có dữ liệu người xem duy nhất, phạm vi tiếp cận, lượt nhấp, tin nhắn hoặc khách hàng tiềm năng.",
        "Hai Reel có thời gian xem trung bình 3 giây. Trường tổng thời gian xem của chúng không được xác minh đơn vị nên không được quy đổi thành giây hoặc sử dụng để tính toán.",
        "Không có dữ liệu theo ngày. Ngày gắn với từng nội dung là ngày đăng, không phải ngày phát sinh tương tác. Không đủ căn cứ xác định tăng trưởng theo thời gian.",
        "Không có dữ liệu chi phí quảng cáo hoặc phân phối để đánh giá hiệu quả trả phí. Thông tin phản hồi từ quảng cáo trong mẫu không đủ để xác định toàn bộ lịch sử quảng cáo.",
        "Chưa xem trực tiếp nội dung video hoặc hình ảnh bài đăng. Các đề xuất về cách mở đầu, hình ảnh và cấu trúc nội dung chỉ là giả thuyết cần thử nghiệm, không phải nguyên nhân đã được xác nhận.",
        "Không nên so sánh trực tiếp tổng lượt cảm xúc giữa Reel và bài viết để kết luận định dạng nào hiệu quả hơn vì số lượng nội dung và dữ liệu tiếp cận khác nhau hoặc còn thiếu."
        ]
        }
        """;

    private sealed class FixtureAdapter : IProviderAdapter
    {
        public string ProviderKey => ProviderKeys.ChatGPTWeb;
        public int Calls { get; private set; }
        public string? Prompt { get; private set; }
        public bool Invalid { get; set; }
        public string? Response { get; set; }
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
            if (Response is not null) return new JobExecutionResult(true, ResultText: Response, SideEffectSubmitted: true);
            var item = new { text = "Thử một chủ đề và theo dõi lượt xem", evidence = new[] { Invalid ? "not-in-snapshot" : "T1" } };
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, summary = "Báo cáo fixture", highlights = new[] { item }, improvements = Array.Empty<object>(),
                recommendations = new[] { item, item, item }, limitations = Enumerable.Repeat("Mẫu số liệu thử nghiệm", 11).ToArray()
            });
            return new JobExecutionResult(true, ResultText: json, SideEffectSubmitted: true);
        }
    }
}
