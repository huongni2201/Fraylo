using System.Collections.Concurrent;
using System.Text.Json;
using Fraylo.Application.Facebook;
using Fraylo.Application.Jobs;
using Fraylo.Application.Providers;
using Fraylo.Application.Settings;
using Fraylo.Browser;
using Fraylo.Contracts.Facebook;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;
using Fraylo.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

internal static class FacebookRewriteSmokeTests
{
    private const string Source = "Mời bạn khám phá bộ sưu tập mới, chất vải mềm và dễ phối đồ khi đi làm hoặc dạo phố. " +
        "Sản phẩm có giá 250.000đ, liên hệ 0901234567 hoặc xem https://example.invalid/shop để chọn mẫu phù hợp. " +
        "Nhắn tin để được tư vấn kích cỡ, giữ đúng thông tin và lời mời trong nội dung này. 😀";

    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fraylo-rewrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS " + label); }
        async Task Throws(Func<Task> action, string label)
        {
            var failed = false;
            try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException) { failed = true; }
            Check(failed, label);
        }
        try
        {
            var path = Path.Combine(root, "test.db");
            await new SqliteDatabaseBootstrapper().BootstrapAsync(path);
            var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite(DatabasePathHelper.BuildConnectionString(path)).AddInterceptors(new SqlitePragmaInterceptor()).Options;
            var profiles = new ProfileRegistry(() => new FrayloDbContext(options), Path.Combine(root, "profiles"));
            var roots = new StorageRootRegistry(options, root);
            await roots.EnsureDefaultRootsAsync();
            var registry = new ProviderRegistry();
            var adapter = new FixtureAdapter(); registry.RegisterAdapter(adapter);
            var reservation = new JobReservation(options);
            await using var scheduler = new JobScheduler(options, reservation, new ProductionJobExecutor(registry));
            var jobs = new JobService(options, new JobSnapshotFactory(new SettingsService(options, roots)), scheduler);
            var rewrite = new FacebookContentRewriteService(options, registry, jobs, scheduler);
            var browser = new FakeBrowser();
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var service = new FacebookPublishingService(options, roots, profiles, browser, new FakeController(), clock, rewrite: rewrite);
            await using (var db = new FrayloDbContext(options))
            {
                db.BrowserProfiles.AddRange(new BrowserProfile { Id = "fb", Kind = "Facebook", ProviderKey = "facebook", AccountIdentity = "Facebook (1)", SessionStatus = "Connected" },
                    new BrowserProfile { Id = "ai", Kind = "ChatGPT", ProviderKey = ProviderKeys.ChatGPTWeb, AccountIdentity = "fixture", SessionStatus = "Connected" });
                db.ProviderAccounts.AddRange(new ProviderAccount { Id = "fb-account", ProfileId = "fb", ProviderKey = "facebook", Enabled = true },
                    new ProviderAccount { Id = "ai-account", ProfileId = "ai", ProviderKey = ProviderKeys.ChatGPTWeb, VerifiedIdentity = "fixture", Enabled = true, MaxTabs = 3 });
                db.AppSettings.AddRange(new AppSetting { Key = SettingsService.KeyFacebookRewriteProvider, Value = ProviderKeys.ChatGPTWeb },
                    new AppSetting { Key = SettingsService.KeyMaxConcurrentJobs, Value = "3" });
                for (var i = 0; i < 50; i++) db.FacebookTargets.Add(new() { Id = "g" + i, ProfileId = "fb", Name = "Nhóm " + i, Url = "https://www.facebook.com/groups/" + i + "/" });
                await db.SaveChangesAsync();
            }
            FacebookCreateBatchRequest Request(int n, string? source = null) => new(Guid.NewGuid().ToString(), "fb", Enumerable.Range(0, n).Select(i => "g" + i).ToArray(), source ?? Source, [], RewriteContent: true);
            async Task<FacebookPostDto> Post(string id) => (await service.ListAsync()).Posts!.Single(p => p.BatchId == id);
            async Task Cancel(string id) { var p = await Post(id); await service.PostActionAsync(new(id, p.Revision, "cancel")); }
            async Task Drain()
            {
                for (var i = 0; i < 300; i++)
                {
                    await scheduler.DispatchPendingJobsAsync();
                    await Task.Delay(20);
                    await using var db = new FrayloDbContext(options);
                    if (!await db.Jobs.AnyAsync(j => j.State == JobState.Queued || j.State == JobState.Reserved || j.State == JobState.Running)) return;
                }
                throw new InvalidOperationException("Queue did not drain: " + scheduler.LastWorkerException);
            }

            foreach (var n in new[] { 1, 9, 10, 11, 23, 50 })
            {
                var request = Request(n);
                var tasks = await service.CreateBatchAsync(request);
                var p = await Post(tasks[0].BatchId);
                Check(p.RewriteChunks!.Count == (n + 9) / 10 && p.RewriteChunks.Sum(c => c.Count) == n && p.RewriteChunks.Last().Count == (n % 10 == 0 ? 10 : n % 10), $"{n} nhóm: đúng lô và không sinh dư");
                Check((await service.CreateBatchAsync(request)).Select(t => t.TaskId).SequenceEqual(tasks.Select(t => t.TaskId)), "Replay không tạo thêm task/job");
                Check(!await service.ProcessNextAsync() && browser.Calls == 0, "Barrier chặn bản gốc trước AI");
                await Throws(() => service.CreateBatchAsync(request with { Text = Source + " khác" }), "Replay nguồn khác bị từ chối");
                await Cancel(p.BatchId);
            }

            await using (var db = new FrayloDbContext(options))
            {
                db.Add(new AnalyticsAnalysisReport { Id = "busy-report", Platform = "tiktok", AccountId = "fixture", SourceId = "fixture" });
                db.Jobs.Add(new() { Id = "busy-analysis", AnalyticsReportId = "busy-report", TaskType = JobTaskType.AnalyticsAnalysis, ProviderKey = ProviderKeys.ChatGPTWeb });
                await db.SaveChangesAsync();
            }
            var busy = await reservation.TryReserveJobAsync("busy-analysis", "fixture", TimeSpan.FromMinutes(5));
            Check(busy.Acquired, "Một job Analytics giữ slot trong cùng pool");
            var fifty = (await service.CreateBatchAsync(Request(50)))[0].BatchId;
            Check(await scheduler.DispatchPendingJobsAsync() == 2, "Capacity ba: Analytics chiếm một, rewrite chỉ nhận hai lô");
            await reservation.CommitSuccessAsync("busy-analysis", busy.Job!.FencingToken, null, "fixture");
            await Drain(); await rewrite.ApplyPendingAsync();
            var ready = await Post(fifty);
            Check(adapter.MaxActive <= 3 && adapter.MaxActive > 1, "Lô AI thực sự song song trong giới hạn ba slot");
            Check(ready.RewriteChunks!.All(c => c.Applied) && ready.Tasks.Select(t => t.Text).Distinct().Count() == 50 && ready.Tasks.All(t => t.Text != Source), "Lưu đủ năm mươi bản riêng");
            var texts = ready.Tasks.Select(t => t.Text).ToArray();
            await rewrite.ApplyPendingAsync();
            Check((await Post(fifty)).Tasks.Select(t => t.Text).SequenceEqual(texts), "Apply lặp không đổi nội dung");
            Check(ready.OriginalText == Source, "Giữ nguồn bất biến cho nhân bản");
            browser.Post = async (input, before) => { Check(input.Text == texts[0], "Adapter nhận đúng nội dung đã thay của nhóm đầu"); await before(); return new(Fraylo.Browser.Publishing.FacebookPostOutcome.Published, null, null); };
            Check(await service.ProcessNextAsync(), "Mở đăng sau khi toàn bài đủ bản AI");
            Check(!await service.ProcessNextAsync() && browser.Calls == 1, "Giữ pacing Facebook sau AI");
            await Cancel(fifty); browser.Post = null;

            await using (var db = new FrayloDbContext(options))
                await db.ProviderAccounts.Where(a => a.Id == "ai-account").ExecuteUpdateAsync(s => s.SetProperty(a => a.MaxTabs, 1));
            var singleCapacity = (await service.CreateBatchAsync(Request(23)))[0].BatchId;
            Check(await scheduler.DispatchPendingJobsAsync() == 1, "Capacity một: chỉ nhận một lô trong ba");
            await Drain(); await rewrite.ApplyPendingAsync(); await Cancel(singleCapacity);
            await using (var db = new FrayloDbContext(options))
                await db.ProviderAccounts.Where(a => a.Id == "ai-account").ExecuteUpdateAsync(s => s.SetProperty(a => a.MaxTabs, 3).SetProperty(a => a.PerTaskLimitsJson, "{\"FacebookContentRewrite\":1}"));
            var taskLimit = (await service.CreateBatchAsync(Request(23)))[0].BatchId;
            Check(await scheduler.DispatchPendingJobsAsync() == 1, "Per-task limit một vẫn áp dụng khi tài khoản có ba slot");
            await Drain(); await rewrite.ApplyPendingAsync(); await Cancel(taskLimit);
            await using (var db = new FrayloDbContext(options))
                await db.ProviderAccounts.Where(a => a.Id == "ai-account").ExecuteUpdateAsync(s => s.SetProperty(a => a.PerTaskLimitsJson, "{}"));

            var paused = (await service.CreateBatchAsync(Request(11)))[0].BatchId;
            var pausedPost = await Post(paused); await service.PostActionAsync(new(paused, pausedPost.Revision, "pause"));
            Check(await scheduler.DispatchPendingJobsAsync() == 0, "Pause chặn reserve AI");
            pausedPost = await Post(paused); await service.PostActionAsync(new(paused, pausedPost.Revision, "resume"));
            await Drain(); await rewrite.ApplyPendingAsync();
            Check((await Post(paused)).RewriteChunks!.All(c => c.Applied), "Resume dùng đúng các lô đã tạo");
            await Cancel(paused);

            var preSend = (await service.CreateBatchAsync(Request(1)))[0].BatchId;
            var prePost = await Post(preSend);
            var reserved = await reservation.TryReserveJobAsync(prePost.RewriteChunks![0].ChunkId, "fixture", TimeSpan.FromMinutes(5));
            await reservation.TransitionToRunningAsync(reserved.Job!.Id, reserved.Job.FencingToken);
            await service.PostActionAsync(new(preSend, prePost.Revision, "pause"));
            var fenced = false;
            try { await reservation.CommitSideEffectAsync(reserved.Job.Id, reserved.Job.FencingToken); } catch (FencingTokenStaleException) { fenced = true; }
            await using (var db = new FrayloDbContext(options))
            {
                Check(fenced && await db.Jobs.AnyAsync(j => j.Id == reserved.Job.Id && j.State == JobState.Queued && j.SideEffectSubmitted == 0) &&
                    !await db.ActiveSlotOccupancies.AnyAsync(o => o.JobId == reserved.Job.Id), "Pause sát Send: không gửi, trả job và nhả slot");
            }
            await Cancel(preSend);

            var expired = (await service.CreateBatchAsync(Request(1)))[0].BatchId;
            var expiredPost = await Post(expired);
            var oldLease = await reservation.TryReserveJobAsync(expiredPost.RewriteChunks![0].ChunkId, "fixture", TimeSpan.FromMinutes(5));
            await reservation.TransitionToRunningAsync(oldLease.Job!.Id, oldLease.Job.FencingToken);
            await using (var db = new FrayloDbContext(options))
            {
                var past = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
                await db.Jobs.Where(j => j.Id == oldLease.Job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAtUtc, past));
            }
            var leaseBlocked = false;
            try { await reservation.CommitSuccessAsync(oldLease.Job.Id, oldLease.Job.FencingToken, null, "late"); } catch (FencingTokenStaleException) { leaseBlocked = true; }
            Check(leaseBlocked, "Worker hết lease không ghi kết quả thành công"); await Cancel(expired);

            // Complete chunks in reverse order, with a duplicate across chunks.
            var reversed = (await service.CreateBatchAsync(Request(11)))[0].BatchId;
            await using (var db = new FrayloDbContext(options))
            {
                var chunks = await db.FacebookRewriteChunks.Include(c => c.Job).Where(c => c.BatchId == reversed).OrderByDescending(c => c.Ordinal).ToListAsync();
                foreach (var chunk in chunks)
                {
                    var input = JsonSerializer.Deserialize<FacebookContentRewriteResultParser.Input>(chunk.Job!.ConfigSnapshotJson!, FacebookContentRewriteResultParser.Json)!;
                    var output = FixtureAdapter.Output(input);
                    if (chunk.Ordinal == 1) output.Variants[0] = output.Variants[0] with { Text = FixtureAdapter.Text("slot-1") };
                    chunk.Job.State = JobState.Succeeded; chunk.Job.ResultText = JsonSerializer.Serialize(output, FacebookContentRewriteResultParser.Json);
                }
                await db.SaveChangesAsync();
            }
            await Task.WhenAll(Task.Run(() => rewrite.ApplyPendingAsync()), Task.Run(() => rewrite.ApplyPendingAsync()));
            var duplicate = await Post(reversed);
            Check(duplicate.RewriteChunks!.Count(c => c.Applied) == 1 && duplicate.RewriteChunks!.Any(c => c.Reason == "RewriteDuplicateResult"), "Từ chối trùng liên lô, giữ lô tốt");
            Check(!await service.ProcessNextAsync(), "Một lô lỗi chặn toàn bài");
            var failedChunk = duplicate.RewriteChunks!.Single(c => !c.Applied);
            await Throws(() => rewrite.RetryAsync(new("foreign", failedChunk.ChunkId, duplicate.Revision), default), "Retry kiểm scope bài/lô");
            await rewrite.RetryAsync(new(reversed, failedChunk.ChunkId, duplicate.Revision), default);
            await Drain(); await rewrite.ApplyPendingAsync();
            Check((await Post(reversed)).RewriteChunks!.All(c => c.Applied), "Retry chỉ lô lỗi, giữ nguyên lô đã nhận");
            await Cancel(reversed);

            var atomic = (await service.CreateBatchAsync(Request(2)))[0].BatchId;
            await using (var db = new FrayloDbContext(options))
            {
                var job = await db.Jobs.SingleAsync(j => j.FacebookRewriteChunk != null && j.FacebookRewriteChunk.BatchId == atomic);
                var input = JsonSerializer.Deserialize<FacebookContentRewriteResultParser.Input>(job.ConfigSnapshotJson!, FacebookContentRewriteResultParser.Json)!;
                job.State = JobState.Succeeded; job.SideEffectSubmitted = 1;
                job.ResultText = JsonSerializer.Serialize(FixtureAdapter.Output(input), FacebookContentRewriteResultParser.Json);
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fbr_apply_failure BEFORE UPDATE OF text_snapshot ON FacebookPostTask WHEN OLD.sort_order = 1 BEGIN SELECT RAISE(ABORT, 'fixture-write-failure'); END;");
            }
            await rewrite.ApplyPendingAsync();
            var failedSave = await Post(atomic);
            Check(failedSave.Tasks.All(t => t.Text == Source) && failedSave.RewriteChunks!.Single() is { Applied: false, Reason: "RewriteSaveFailed" }, "Lỗi ghi giữa lô rollback toàn bộ text và giữ phản hồi AI");
            await using (var db = new FrayloDbContext(options)) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fbr_apply_failure;");
            await rewrite.RetryAsync(new(atomic, failedSave.RewriteChunks![0].ChunkId, failedSave.Revision), default);
            Check((await Post(atomic)).RewriteChunks!.Single().Applied && await scheduler.DispatchPendingJobsAsync() == 0, "Sau lỗi ghi, lưu lại từ raw response mà không gửi AI lần nữa");
            await Cancel(atomic);

            var bad = (await service.CreateBatchAsync(Request(10)))[0].BatchId;
            adapter.Invalid = true; await Drain(); adapter.Invalid = false;
            var badPost = await Post(bad);
            Check(badPost.RewriteChunks!.Single().Reason == FacebookContentRewriteResultParser.InvalidResult && badPost.Tasks.All(t => t.Text == Source), "JSON sai không thay nội dung gốc");
            Check(await scheduler.DispatchPendingJobsAsync() == 0, "Lỗi parser không tự retry");
            await rewrite.RetryAsync(new(bad, badPost.RewriteChunks![0].ChunkId, badPost.Revision), default); await Drain(); await rewrite.ApplyPendingAsync();
            Check((await Post(bad)).RewriteChunks!.Single().Applied, "Retry thủ công sau lỗi parser"); await Cancel(bad);

            var restarted = (await service.CreateBatchAsync(Request(1)))[0].BatchId;
            await rewrite.HoldUnsentOnStartupAsync(default);
            Check((await Post(restarted)).RewriteChunks!.Single().State == JobState.AwaitingUser && await scheduler.DispatchPendingJobsAsync() == 0, "Restart giữ job chưa gửi chờ xác nhận");
            await Cancel(restarted);

            var uncertain = (await service.CreateBatchAsync(Request(1)))[0].BatchId;
            adapter.Uncertain = true; await Drain(); adapter.Uncertain = false;
            var uncertainPost = await Post(uncertain);
            Check(uncertainPost.RewriteChunks!.Single() is { Uncertain: true, CanRetry: false }, "Timeout sau submit giữ checkpoint, không cho gửi lại mù");
            await Throws(() => rewrite.RetryAsync(new(uncertain, uncertainPost.RewriteChunks![0].ChunkId, uncertainPost.Revision), default), "Retry chặn lô chưa rõ kết quả");
            Check(await scheduler.DispatchPendingJobsAsync() == 0, "Uncertain không tự resend");
            await Cancel(uncertain);

            var scheduledRequest = Request(1) with { DueAtUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O") };
            var scheduled = (await service.CreateBatchAsync(scheduledRequest))[0].BatchId;
            await Drain(); await rewrite.ApplyPendingAsync();
            var beforeCalls = browser.Calls;
            Check(!await service.ProcessNextAsync() && browser.Calls == beforeCalls, "AI trước lịch không mở Facebook"); await Cancel(scheduled);

            var missed = (await service.CreateBatchAsync(scheduledRequest with { BatchRequestId = Guid.NewGuid().ToString() }))[0].BatchId;
            clock.Current = clock.Current.AddHours(2);
            var restartedService = new FacebookPublishingService(options, roots, profiles, browser, new FakeController(), clock, rewrite: rewrite);
            await restartedService.RecoverInterruptedAsync();
            await Drain(); await rewrite.ApplyPendingAsync();
            Check((await Post(missed)).Tasks.All(t => t.State == "NeedsUser" && t.ReasonCode == "schedule_missed") && !await restartedService.ProcessNextAsync(), "AI hoàn tất không bỏ trạng thái lỡ lịch sau restart");
            await Cancel(missed);

            var canceling = (await service.CreateBatchAsync(Request(10)))[0].BatchId;
            adapter.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await scheduler.DispatchPendingJobsAsync();
            for (var i = 0; i < 100 && adapter.Active == 0; i++) await Task.Delay(10);
            await Cancel(canceling); adapter.Hold.TrySetResult(); adapter.Hold = null;
            await Task.Delay(100); await rewrite.ApplyPendingAsync();
            Check((await Post(canceling)).Tasks.All(t => t.State == "Canceled" && t.Text == Source), "Cancel chặn apply kết quả tới muộn");

            await Throws(() => service.CreateBatchAsync(Request(10, new string('a', 7000))), "Giới hạn input/output không cắt nguồn");
            await Throws(() => service.CreateBatchAsync(Request(10, new string('a', 2500))), "Từ chối riêng giới hạn output khi input vẫn vừa");
            await using (var db = new FrayloDbContext(options)) await db.AppSettings.Where(s => s.Key == SettingsService.KeyFacebookRewriteProvider).ExecuteDeleteAsync();
            await Throws(() => service.CreateBatchAsync(Request(1)), "Thiếu provider không tạo bài có thể đăng bản gốc");
            await Throws(() => service.CreateBatchAsync(Request(1) with { Text = "" }), "Giữ validation bài rỗng không ảnh");
            var originalRequest = Request(2) with { RewriteContent = false };
            var originalTasks = await service.CreateBatchAsync(originalRequest);
            Check(originalTasks.All(t => t.Text == Source) && (await Post(originalTasks[0].BatchId)).RewriteChunks == null,
                "Đăng nguyên bản giữ chữ từng nhóm, không cần provider và không tạo lô AI");
            await Throws(() => service.CreateBatchAsync(originalRequest with { RewriteContent = true }), "Replay đổi cách đăng bị từ chối");
            await using (var db = new FrayloDbContext(options))
                Check(!await db.FacebookRewriteChunks.AnyAsync(c => c.BatchId == originalTasks[0].BatchId), "Nguyên bản không tạo job AI trong SQLite");
            var originalService = new FacebookPublishingService(options, roots, profiles, browser, new FakeController(), clock, rewrite: rewrite);
            clock.Current += TimeSpan.FromMinutes(6);
            string? postedText = null;
            browser.Post = async (input, before) => { postedText = input.Text; await before(); return new(Fraylo.Browser.Publishing.FacebookPostOutcome.Published, null, "published"); };
            Check(await originalService.ProcessNextAsync() && postedText == Source, "Khởi động service mới vẫn đăng đúng nguyên bản, không chờ AI");
            await Cancel(originalTasks[0].BatchId);
            var scheduledOriginal = await service.CreateBatchAsync(Request(1) with { RewriteContent = false, DueAtUtc = clock.Current.AddHours(1).ToString("O") });
            var beforeOriginalCalls = browser.Calls;
            Check(!await service.ProcessNextAsync() && browser.Calls == beforeOriginalCalls && scheduledOriginal[0].Text == Source,
                "Nguyên bản hẹn giờ không mở Facebook trước lịch");
            await Cancel(scheduledOriginal[0].BatchId);
            var draftRequest = new FacebookSaveDraftRequest(null, "Nháp nguyên bản", "fb", Source, ["g0"], [], RewriteContent: false);
            var savedDraft = await service.SaveDraftAsync(draftRequest);
            Check(!(await originalService.ListAsync()).Drafts!.Single(d => d.DraftId == savedDraft.DraftId).RewriteContent,
                "Nháp giữ lựa chọn nguyên bản qua service mới");
            Check((await service.SaveDraftAsync(draftRequest with { DraftId = savedDraft.DraftId, RewriteContent = true })).RewriteContent,
                "Cập nhật nháp đổi về viết lại");
            var withoutRewrite = new FacebookPublishingService(options, roots, profiles, browser, new FakeController(), clock);
            await Throws(() => withoutRewrite.CreateBatchAsync(Request(1)), "Viết lại không được âm thầm đăng nguyên bản khi service AI thiếu");
            var parserInput = new FacebookContentRewriteResultParser.Input(1, "batch", Source, ["slot-1", "slot-2"], (int)Math.Ceiling(Source.EnumerateRunes().Count() * .85), (int)Math.Floor(Source.EnumerateRunes().Count() * 1.15));
            var inputJson = JsonSerializer.Serialize(parserInput, FacebookContentRewriteResultParser.Json);
            var valid = FixtureAdapter.Output(parserInput);
            string Json(FacebookContentRewriteResultParser.Output value) => JsonSerializer.Serialize(value, FacebookContentRewriteResultParser.Json);
            Check(FacebookContentRewriteResultParser.Parse("```json\n" + Json(valid) + "\n```", inputJson) != null, "JSON code fence/emoji, mapping response đảo thứ tự");
            foreach (var badOutput in new[]
            {
                valid with { SchemaVersion = 2 }, valid with { Variants = [valid.Variants[0]] },
                valid with { Variants = [valid.Variants[0], valid.Variants[0]] },
                valid with { Variants = [valid.Variants[0] with { SlotId = "foreign" }, valid.Variants[1]] },
                valid with { Variants = [valid.Variants[0] with { Text = Source }, valid.Variants[1]] },
                valid with { Variants = [valid.Variants[0] with { Text = "ngắn" }, valid.Variants[1]] },
                valid with { Variants = [valid.Variants[0] with { Text = valid.Variants[0].Text.Replace("250.000", "350.000") }, valid.Variants[1]] },
                valid with { Variants = [valid.Variants[0] with { Text = valid.Variants[0].Text.Replace("example.invalid", "foreign.invalid") }, valid.Variants[1]] },
                valid with { Variants = [valid.Variants[0] with { Text = valid.Variants[0].Text + " 99" }, valid.Variants[1]] },
                valid with { Variants = [valid.Variants[0] with { Text = valid.Variants[0].Text + "\u200b" }, valid.Variants[1]] },
            }) Check(FacebookContentRewriteResultParser.Parse(Json(badOutput), inputJson) == null, "Parser loại schema/slot/trùng/độ dài/sai dữ kiện/link/ký tự ẩn");
            Console.WriteLine($"Facebook rewrite: {passed} PASS; SQLite + shared scheduler + fake provider/browser. Live NOT_RUN.");
            return 0;
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class FixtureAdapter : IProviderAdapter
    {
        public string ProviderKey => ProviderKeys.ChatGPTWeb;
        public int Active, MaxActive;
        public bool Invalid, Uncertain;
        public TaskCompletionSource? Hold;
        public IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities() => [new(ProviderKey, JobTaskType.FacebookContentRewrite, ProviderVerificationStatus.Verified, true, false, false, 8000, null, null, 1, MaxOutputLength: 24000)];
        public Task<ProviderTaskCapabilityDto?> GetCapabilityForTaskAsync(string taskType, CancellationToken ct = default) => Task.FromResult(GetCapabilities().FirstOrDefault(c => c.TaskType == taskType));
        public static string Text(string slot) => Source.Replace("Mời bạn", "Cùng khám phá " + (char)('a' + int.Parse(slot[5..]) / 26) + (char)('a' + int.Parse(slot[5..]) % 26), StringComparison.Ordinal);
        public static FacebookContentRewriteResultParser.Output Output(FacebookContentRewriteResultParser.Input input) => new(1, input.Slots.Reverse().Select(s => new FacebookContentRewriteResultParser.Variant(s, Text(s))).ToArray());
        public async Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, Func<Task> before, Func<int, string, Task>? progress, CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref Active); MaxActive = Math.Max(active, MaxActive);
            try
            {
                await before();
                if (Hold is { } hold) await hold.Task.WaitAsync(ct); else await Task.Delay(80, ct);
                if (Uncertain) return new(false, ReasonCode: "network_error", SideEffectSubmitted: true);
                var input = JsonSerializer.Deserialize<FacebookContentRewriteResultParser.Input>(context.ConfigSnapshotJson!, FacebookContentRewriteResultParser.Json)!;
                return new(true, ResultText: Invalid ? "invalid-json" : JsonSerializer.Serialize(Output(input), FacebookContentRewriteResultParser.Json), SideEffectSubmitted: true);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
