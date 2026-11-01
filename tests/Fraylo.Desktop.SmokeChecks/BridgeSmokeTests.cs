using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Contracts.Bridge;
using Fraylo.Desktop.Bridge;
using Fraylo.Desktop.Hosting;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Smoke checks kiểm tra WebView2 Bridge Router, Dispatcher và Theme Service.
/// </summary>
public static class BridgeSmokeTests
{
    private sealed class MockDispatcher : IBridgeDispatcher
    {
        private readonly Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>> _handler;

        public MockDispatcher(Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>> handler)
        {
            _handler = handler;
        }

        public bool IsCommandAllowed(string command) => true;

        public void AllowCommand(string command) { }

        public void RegisterHandler(string command, Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>> handler) { }

        public bool UnregisterHandler(string command) => true;

        public Task<ResponseEnvelope> DispatchAsync(RequestEnvelope request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    /// <summary>
    /// Test Case 1: JSON envelope parser and dispatcher smoke check
    /// Kiểm tra router phân tích cú pháp JSON envelope hợp lệ (như trích xuất từ WebMessageAsJson hoặc postMessage)
    /// và điều phối thành công tới dispatcher.
    /// Lưu ý: Đây là unit smoke check ở tầng parser/dispatcher; kiểm tra WebView2 COM event args
    /// (fallback E_INVALIDARG -> WebMessageAsJson) và WebView2 runtime thật sẽ do coordinator chạy trên host.
    /// </summary>
    public static async Task Test_JsonEnvelopeParserAndDispatcher_Success()
    {
        var dispatcher = new MockDispatcher((req, ct) =>
        {
            return Task.FromResult(ResponseEnvelopeHelper.CreateSuccess(req.Id, new { status = "ok" }));
        });

        var router = new BridgeMessageRouter(dispatcher);
        var reqId = Guid.NewGuid();
        string objectJson = $$"""
        {
            "id": "{{reqId}}",
            "type": "request",
            "command": "settings.get",
            "payload": {},
            "clientTimestampUtc": "{{DateTimeOffset.UtcNow:O}}",
            "contractVersion": "1.1.0"
        }
        """;

        var response = await router.ProcessMessageAsync(BridgeContract.Origin, objectJson);
        Assert(response != null, "Response không được null");
        Assert(response!.Success, "Response phải thành công");
        Assert(response.Id == reqId, "Response Id phải khớp với Request Id");
        Assert(response.ContractVersion == "1.1.0", "Contract version phải là 1.1.0");
    }

    /// <summary>
    /// Test Case 2: Version mismatch trên cancel message
    /// Cancel message sai version (vd: 1.0.0) phải bị từ chối với INVALID_ARGUMENT
    /// và tuyệt đối không hủy request đang chạy của version 1.1.0.
    /// </summary>
    public static async Task Test_CancelMessage_VersionMismatch_DoesNotCancelInFlight()
    {
        var runningTcs = new TaskCompletionSource<bool>();
        var canceledTcs = new TaskCompletionSource<bool>();

        var dispatcher = new MockDispatcher(async (req, ct) =>
        {
            runningTcs.TrySetResult(true);
            try
            {
                await Task.Delay(5000, ct);
                return ResponseEnvelopeHelper.CreateSuccess(req.Id);
            }
            catch (OperationCanceledException)
            {
                canceledTcs.TrySetResult(true);
                throw;
            }
        });

        var router = new BridgeMessageRouter(dispatcher);
        var targetId = Guid.NewGuid();

        string requestJson = $$"""
        {
            "id": "{{targetId}}",
            "type": "request",
            "command": "job.enqueue",
            "payload": { "projectId": "project-1" },
            "contractVersion": "1.1.0"
        }
        """;

        var reqTask = router.ProcessMessageAsync(BridgeContract.Origin, requestJson);
        var started = await Task.WhenAny(runningTcs.Task, Task.Delay(3000)) == runningTcs.Task;
        Assert(started, "Request mục tiêu phải bắt đầu chạy trong giới hạn thời gian");

        // Gửi cancel message với version cũ 1.0.0
        var cancelId = Guid.NewGuid();
        string invalidCancelJson = $$"""
        {
            "id": "{{cancelId}}",
            "type": "cancel",
            "targetRequestId": "{{targetId}}",
            "contractVersion": "1.0.0"
        }
        """;

        var cancelResponse = await router.ProcessMessageAsync(BridgeContract.Origin, invalidCancelJson);
        Assert(cancelResponse != null, "Cancel message sai version phải trả về response lỗi");
        Assert(!cancelResponse!.Success, "Cancel response phải thất bại");
        Assert(cancelResponse.Error?.Code == "INVALID_ARGUMENT", "Mã lỗi phải là INVALID_ARGUMENT");
        Assert(!canceledTcs.Task.IsCompleted, "Request đang chạy không được bị hủy bởi cancel sai version");

        // Dọn dẹp router và chờ reqTask kết thúc để tránh rò rỉ tác vụ nền
        router.Dispose();
        await Task.WhenAny(reqTask, Task.Delay(2000));
    }

    /// <summary>
    /// Test Case 3: Duplicate request ID
    /// Gửi 2 request cùng requestId song song: request thứ hai bị từ chối với CONCURRENCY_CONFLICT
    /// và request đầu tiên hoàn tất bình thường mà không bị xóa nhầm registration.
    /// </summary>
    public static async Task Test_DuplicateRequestId_RejectedWithConcurrencyConflict()
    {
        var firstStarted = new TaskCompletionSource<bool>();
        var firstBlocker = new TaskCompletionSource<bool>();
        var dispatcher = new MockDispatcher(async (req, ct) =>
        {
            firstStarted.TrySetResult(true);
            await firstBlocker.Task;
            return ResponseEnvelopeHelper.CreateSuccess(req.Id, new { completed = true });
        });

        var router = new BridgeMessageRouter(dispatcher);
        var duplicateId = Guid.NewGuid();

        string reqJson = $$"""
        {
            "id": "{{duplicateId}}",
            "type": "request",
            "command": "project.get",
            "payload": { "projectId": "project-test" },
            "contractVersion": "1.1.0"
        }
        """;

        var task1 = router.ProcessMessageAsync(BridgeContract.Origin, reqJson);
        var started = await Task.WhenAny(firstStarted.Task, Task.Delay(3000)) == firstStarted.Task;
        Assert(started, "Request 1 phải bắt đầu chạy");

        // Gửi request thứ 2 ngay lập tức với cùng ID
        var response2 = await router.ProcessMessageAsync(BridgeContract.Origin, reqJson);
        Assert(response2 != null, "Response 2 không được null");
        Assert(!response2!.Success, "Response 2 phải thất bại");
        Assert(response2.Error?.Code == "CONCURRENCY_CONFLICT", "Response 2 phải có mã CONCURRENCY_CONFLICT");
        Assert(response2.Error?.Retryable == true, "CONCURRENCY_CONFLICT phải có retryable = true");

        // Cho task 1 hoàn tất
        firstBlocker.TrySetResult(true);
        var completed = await Task.WhenAny(task1, Task.Delay(3000)) == task1;
        Assert(completed, "Request 1 phải hoàn tất trong giới hạn thời gian");
        var response1 = await task1;
        Assert(response1 != null && response1.Success, "Request 1 phải thành công");
    }

    /// <summary>
    /// Test Case 4: Nested secret arrays trong payload
    /// HasProhibitedSecurityFields phải duyệt đệ quy array lồng nhau và chặn token/cookie/userDataDir
    /// nhưng cho phép selectedPath và relativeStoragePath.
    /// </summary>
    public static async Task Test_ProhibitedFields_NestedArraysAndAllowedFields()
    {
        var dispatcher = new MockDispatcher((req, ct) =>
            Task.FromResult(ResponseEnvelopeHelper.CreateSuccess(req.Id)));

        var router = new BridgeMessageRouter(dispatcher);

        // 1. Array chứa cookie
        string jsonWithArrayCookie = $$"""
        {
            "id": "{{Guid.NewGuid()}}",
            "type": "request",
            "command": "settings.save",
            "payload": {
                "items": [
                    { "name": "item1", "cookie": "secret_cookie_val" }
                ]
            },
            "contractVersion": "1.1.0"
        }
        """;
        var res1 = await router.ProcessMessageAsync(BridgeContract.Origin, jsonWithArrayCookie);
        Assert(res1 != null && !res1.Success && res1.Error?.Code == "INVALID_ARGUMENT",
            "Payload chứa cookie trong array phải bị chặn");

        // 2. Mảng lồng nhau chứa token
        string jsonWithNestedArrayToken = $$"""
        {
            "id": "{{Guid.NewGuid()}}",
            "type": "request",
            "command": "settings.save",
            "payload": {
                "matrix": [
                    [ { "accessToken": "bearer_secret" } ]
                ]
            },
            "contractVersion": "1.1.0"
        }
        """;
        var res2 = await router.ProcessMessageAsync(BridgeContract.Origin, jsonWithNestedArrayToken);
        Assert(res2 != null && !res2.Success && res2.Error?.Code == "INVALID_ARGUMENT",
            "Payload chứa accessToken trong nested array phải bị chặn");

        // 3. Mảng đối tượng lồng nhau chứa userDataDir
        string jsonWithNestedUserDataDir = $$"""
        {
            "id": "{{Guid.NewGuid()}}",
            "type": "request",
            "command": "settings.save",
            "payload": {
                "profiles": [
                    { "name": "channel_1", "userDataDir": "C:\\Users\\Admin\\AppData\\Local\\Fraylo\\Profile" }
                ]
            },
            "contractVersion": "1.1.0"
        }
        """;
        var res3 = await router.ProcessMessageAsync(BridgeContract.Origin, jsonWithNestedUserDataDir);
        Assert(res3 != null && !res3.Success && res3.Error?.Code == "INVALID_ARGUMENT",
            "Payload chứa userDataDir trong nested array phải bị chặn");

        // 4. Mảng nhiều cấp lồng nhau chứa shell command (cmd)
        string jsonWithNestedCmd = $$"""
        {
            "id": "{{Guid.NewGuid()}}",
            "type": "request",
            "command": "settings.save",
            "payload": {
                "pipeline": [
                    [ { "cmd": "format c:" } ]
                ]
            },
            "contractVersion": "1.1.0"
        }
        """;
        var res4 = await router.ProcessMessageAsync(BridgeContract.Origin, jsonWithNestedCmd);
        Assert(res4 != null && !res4.Success && res4.Error?.Code == "INVALID_ARGUMENT",
            "Payload chứa cmd trong nested array phải bị chặn");

        // 5. Trường hợp lệ: selectedPath và relativeStoragePath
        string jsonWithAllowedPaths = $$"""
        {
            "id": "{{Guid.NewGuid()}}",
            "type": "request",
            "command": "settings.browse_folder",
            "payload": {
                "selectedPath": "D:\\Fraylo\\Media",
                "relativeStoragePath": "audio/channel_01.mp3"
            },
            "contractVersion": "1.1.0"
        }
        """;
        var res5 = await router.ProcessMessageAsync(BridgeContract.Origin, jsonWithAllowedPaths);
        Assert(res5 != null && res5.Success,
            "Payload chứa selectedPath và relativeStoragePath phải được chấp nhận");
    }

    /// <summary>
    /// Test Case 5: System theme fallback
    /// Preference mặc định là "system"; không ép dark nếu không đọc được Windows theme;
    /// preference không bị biến đổi khi resolve theme hiệu lực.
    /// </summary>
    public static void Test_SystemTheme_DefaultAndFallback()
    {
        var themeService = new HostThemeService();
        Assert(themeService.CurrentPreference == "system", "Preference mặc định phải là 'system'");

        // Effective theme phải là "light" hoặc "dark" hợp lệ theo Windows
        string effective = themeService.CurrentEffectiveTheme;
        Assert(effective == "light" || effective == "dark", "Effective theme phải là 'light' hoặc 'dark'");

        // GetEffectiveTheme với "system" không làm đổi preference
        string resolved = themeService.GetEffectiveTheme("system");
        Assert(themeService.CurrentPreference == "system", "Gọi GetEffectiveTheme('system') không được làm đổi CurrentPreference");
        Assert(resolved == "light" || resolved == "dark", "Theme trả về từ Windows phải hợp lệ");

        // Fallback test
        string fallback = HostThemeService.ResolveWindowsTheme();
        Assert(fallback == "light" || fallback == "dark", "ResolveWindowsTheme phải trả về theme an toàn");
    }

    /// <summary>
    /// Test Case 6: Allowed command nhưng chưa có handler
    /// BridgeDispatcher phải trả về thông báo lỗi trung tính và TUYỆT ĐỐI không chứa mã task nội bộ (T04, T06a, T07).
    /// </summary>
    public static async Task Test_BridgeDispatcher_AllowedCommandWithoutHandler_ReturnsNeutralMessageWithoutTaskIds()
    {
        var dispatcher = new BridgeDispatcher();
        var reqId = Guid.NewGuid();
        var request = new RequestEnvelope(
            Id: reqId,
            Type: "request",
            Command: "settings.get",
            Payload: default,
            ClientTimestampUtc: DateTimeOffset.UtcNow,
            ContractVersion: "1.1.0"
        );

        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);
        Assert(response != null, "Response không được null");
        Assert(!response!.Success, "Response phải thất bại khi command chưa có handler");
        Assert(response.Error?.Code == "INTERNAL_ERROR", "Mã lỗi phải là INTERNAL_ERROR");
        Assert(!string.IsNullOrEmpty(response.Error?.Message), "Thông báo lỗi không được rỗng");

        string message = response.Error!.Message;
        Assert(!message.Contains("T04", StringComparison.OrdinalIgnoreCase), "Thông báo không được chứa task ID T04");
        Assert(!message.Contains("T06", StringComparison.OrdinalIgnoreCase), "Thông báo không được chứa task ID T06");
        Assert(!message.Contains("T07", StringComparison.OrdinalIgnoreCase), "Thông báo không được chứa task ID T07");
        Assert(message.Contains("chưa khả dụng", StringComparison.OrdinalIgnoreCase), "Thông báo phải mang tính trung tính ('chưa khả dụng')");
    }

    /// <summary>
    /// Test Case 7: Lệnh không thuộc danh mục allowlist
    /// BridgeDispatcher phải từ chối với mã UNKNOWN_COMMAND.
    /// </summary>
    public static async Task Test_BridgeDispatcher_UnknownCommand_Rejected()
    {
        var dispatcher = new BridgeDispatcher();
        var reqId = Guid.NewGuid();
        var request = new RequestEnvelope(
            Id: reqId,
            Type: "request",
            Command: "system.execute_bash",
            Payload: default,
            ClientTimestampUtc: DateTimeOffset.UtcNow,
            ContractVersion: "1.1.0"
        );

        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);
        Assert(response != null, "Response không được null");
        Assert(!response!.Success, "Lệnh không hợp lệ phải bị từ chối");
        Assert(response.Error?.Code == "UNKNOWN_COMMAND", "Mã lỗi phải là UNKNOWN_COMMAND");
    }

    /// <summary>
    /// Test Case 8: Ngoại lệ nội bộ không làm lộ raw exception message hay đường dẫn hệ thống
    /// BridgeMessageRouter phải trả về thông báo chung kèm correlation ID, không làm lộ secret hoặc đường dẫn file cục bộ.
    /// </summary>
    public static async Task Test_BridgeMessageRouter_InternalException_DoesNotLeakRawMessageOrPaths()
    {
        const string secretPath = @"C:\Users\SecretUser\AppData\Local\Fraylo\secrets.db";
        const string secretToken = "super_secret_token_12345";

        var dispatcher = new MockDispatcher((req, ct) =>
        {
            throw new System.IO.IOException($"Failed to access database at '{secretPath}' with token={secretToken}");
        });

        var router = new BridgeMessageRouter(dispatcher);
        var reqId = Guid.NewGuid();
        string requestJson = $$"""
        {
            "id": "{{reqId}}",
            "type": "request",
            "command": "settings.get",
            "payload": {},
            "contractVersion": "1.1.0"
        }
        """;

        var response = await router.ProcessMessageAsync(BridgeContract.Origin, requestJson);
        Assert(response != null, "Response không được null");
        Assert(!response!.Success, "Response phải thất bại khi xảy ra ngoại lệ nội bộ");
        Assert(response.Error?.Code == "INTERNAL_ERROR", "Mã lỗi phải là INTERNAL_ERROR");

        string message = response.Error!.Message;
        Assert(!message.Contains(secretPath, StringComparison.OrdinalIgnoreCase), "Thông báo lỗi không được chứa đường dẫn máy cục bộ");
        Assert(!message.Contains(secretToken, StringComparison.OrdinalIgnoreCase), "Thông báo lỗi không được chứa secret token");
        Assert(message.Contains(reqId.ToString("D"), StringComparison.OrdinalIgnoreCase), "Thông báo lỗi phải chứa correlation ID để đối soát");
    }

    /// <summary>
    /// Test Case 12: Bounded cancellation smoke check khi Router bị Dispose
    /// Khi BridgeMessageRouter bị Dispose trong lúc có request đang chạy nền dài hạn,
    /// in-flight CancellationToken phải được kích hoạt hủy (OperationCanceledException)
    /// và task kết thúc trong giới hạn thời gian (bounded timeout).
    /// </summary>
    public static async Task Test_Router_Dispose_CancelsInFlightRequest_Bounded()
    {
        var taskStartedTcs = new TaskCompletionSource<bool>();
        var taskCanceledTcs = new TaskCompletionSource<bool>();

        var dispatcher = new MockDispatcher(async (req, ct) =>
        {
            taskStartedTcs.TrySetResult(true);
            try
            {
                await Task.Delay(10000, ct);
                return ResponseEnvelopeHelper.CreateSuccess(req.Id);
            }
            catch (OperationCanceledException)
            {
                taskCanceledTcs.TrySetResult(true);
                throw;
            }
        });

        var router = new BridgeMessageRouter(dispatcher);
        var reqId = Guid.NewGuid();
        string requestJson = $$"""
        {
            "id": "{{reqId}}",
            "type": "request",
            "command": "settings.get",
            "payload": {},
            "contractVersion": "1.1.0"
        }
        """;

        var runTask = router.ProcessMessageAsync(BridgeContract.Origin, requestJson);

        var started = await Task.WhenAny(taskStartedTcs.Task, Task.Delay(2000)) == taskStartedTcs.Task;
        Assert(started, "Request phải bắt đầu xử lý trước khi router bị dispose");

        router.Dispose();

        var canceled = await Task.WhenAny(taskCanceledTcs.Task, Task.Delay(2000)) == taskCanceledTcs.Task;
        Assert(canceled, "Task đang chạy phải bị hủy kịp thời khi router bị Dispose");

        var completed = await Task.WhenAny(runTask, Task.Delay(2000)) == runTask;
        Assert(completed, "Run task phải hoàn tất trong giới hạn thời gian sau khi hủy");

        var response = await runTask;
        Assert(response != null, "Response sau khi hủy không được null");
        Assert(!response!.Success, "Response của request bị hủy phải đánh dấu thất bại");
        Assert(response.Error?.Code == "INTERNAL_ERROR", "Mã lỗi khi bị hủy phải là INTERNAL_ERROR");
    }

    /// <summary>
    /// Test Case 13: Các lệnh gọi tới Router sau khi Dispose phải trả về null và không ném ngoại lệ
    /// </summary>
    public static async Task Test_Router_CallsAfterDispose_ReturnNull()
    {
        var dispatcher = new MockDispatcher((req, ct) =>
            Task.FromResult(ResponseEnvelopeHelper.CreateSuccess(req.Id, new { ok = true })));

        var router = new BridgeMessageRouter(dispatcher);
        router.Dispose();

        // Dispose lại lần nữa để kiểm tra tính idempotent
        router.Dispose();

        var reqId = Guid.NewGuid();
        string requestJson = $$"""
        {
            "id": "{{reqId}}",
            "type": "request",
            "command": "settings.get",
            "payload": {},
            "contractVersion": "1.1.0"
        }
        """;

        var response = await router.ProcessMessageAsync(BridgeContract.Origin, requestJson);
        Assert(response == null, "Lệnh gọi ProcessMessageAsync sau khi Dispose phải trả về null");

        // Gọi với origin không hợp lệ sau dispose cũng trả về null
        var nullOriginResponse = await router.ProcessMessageAsync(null, requestJson);
        Assert(nullOriginResponse == null, "Origin không hợp lệ sau khi dispose phải trả về null");

        // PostResponseAsync và PostEventAsync không ném ngoại lệ sau dispose
        await router.PostResponseAsync(ResponseEnvelopeHelper.CreateSuccess(reqId));
        await router.PostEventAsync("test.event", new { val = 1 });
    }

    /// <summary>
    /// Test Case 14: Thực thi đồng thời Start và Dispose không gây ObjectDisposedException
    /// </summary>
    public static async Task Test_Router_ConcurrentStartAndDispose_NoObjectDisposedException()
    {
        var dispatcher = new MockDispatcher(async (req, ct) =>
        {
            await Task.Yield();
            return ResponseEnvelopeHelper.CreateSuccess(req.Id, new { status = "active" });
        });

        var router = new BridgeMessageRouter(dispatcher);
        var tasks = new Task[30];

        for (int i = 0; i < tasks.Length; i++)
        {
            var reqId = Guid.NewGuid();
            string reqJson = $$"""
            {
                "id": "{{reqId}}",
                "type": "request",
                "command": "settings.get",
                "payload": {},
                "contractVersion": "1.1.0"
            }
            """;

            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    var res = await router.ProcessMessageAsync(BridgeContract.Origin, reqJson);
                }
                catch (ObjectDisposedException ode)
                {
                    Assert(false, $"Ném ObjectDisposedException ngoài dự kiến: {ode.Message}");
                }
            });
        }

        // Gọi dispose đồng thời với các task
        await Task.Yield();
        router.Dispose();

        var allTasks = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(allTasks, Task.Delay(5000)) == allTasks;
        Assert(completed, "Tất cả các task chạy đồng thời phải hoàn tất trong giới hạn thời gian");
        await allTasks;
    }

    /// <summary>
    /// Test Case 15: Valid cancel message hủy in-flight request và dọn dẹp registration
    /// Gửi bản tin type=cancel hợp lệ (contractVersion 1.1.0) tới một request đang chạy:
    /// - Router kích hoạt CancellationToken của request đang chạy
    /// - Router trả về null cho bản tin cancel (theo đúng contract v1.1.0, cancel là control message không sinh response envelope)
    /// - Request đang chạy hoàn tất với mã lỗi INTERNAL_ERROR và thông báo bị hủy trong giới hạn thời gian (bounded timeout)
    /// - Đảm bảo registration trong router được dọn dẹp sạch sẽ để ID đó có thể tái sử dụng mà không bị CONCURRENCY_CONFLICT
    /// </summary>
    public static async Task Test_CancelMessage_ValidCancel_CancelsInFlightRequest_AndCleansUp()
    {
        var runningTcs = new TaskCompletionSource<bool>();
        var canceledTcs = new TaskCompletionSource<bool>();
        int callCount = 0;

        var dispatcher = new MockDispatcher(async (req, ct) =>
        {
            int currentCall = Interlocked.Increment(ref callCount);
            if (currentCall == 1)
            {
                runningTcs.TrySetResult(true);
                try
                {
                    await Task.Delay(10000, ct);
                    return ResponseEnvelopeHelper.CreateSuccess(req.Id);
                }
                catch (OperationCanceledException)
                {
                    canceledTcs.TrySetResult(true);
                    throw;
                }
            }

            return ResponseEnvelopeHelper.CreateSuccess(req.Id, new { reused = true });
        });

        using var router = new BridgeMessageRouter(dispatcher);
        var targetId = Guid.NewGuid();

        string requestJson = $$"""
        {
            "id": "{{targetId}}",
            "type": "request",
            "command": "settings.get",
            "payload": {},
            "contractVersion": "1.1.0"
        }
        """;

        var reqTask = router.ProcessMessageAsync(BridgeContract.Origin, requestJson);

        var started = await Task.WhenAny(runningTcs.Task, Task.Delay(3000)) == runningTcs.Task;
        Assert(started, "Request mục tiêu phải bắt đầu chạy");

        var cancelId = Guid.NewGuid();
        string validCancelJson = $$"""
        {
            "id": "{{cancelId}}",
            "type": "cancel",
            "targetRequestId": "{{targetId}}",
            "contractVersion": "1.1.0"
        }
        """;

        var cancelResponse = await router.ProcessMessageAsync(BridgeContract.Origin, validCancelJson);
        Assert(cancelResponse == null, "Cancel message hợp lệ phải trả về null");

        var wasCanceled = await Task.WhenAny(canceledTcs.Task, Task.Delay(3000)) == canceledTcs.Task;
        Assert(wasCanceled, "CancellationToken của request mục tiêu phải được kích hoạt hủy");

        var reqCompleted = await Task.WhenAny(reqTask, Task.Delay(3000)) == reqTask;
        Assert(reqCompleted, "Request bị hủy phải hoàn tất trong giới hạn thời gian");

        var targetResponse = await reqTask;
        Assert(targetResponse != null, "Response của request bị hủy không được null");
        Assert(!targetResponse!.Success, "Response của request bị hủy phải đánh dấu thất bại");
        Assert(targetResponse.Error?.Code == "INTERNAL_ERROR", "Mã lỗi khi bị hủy phải là INTERNAL_ERROR");
        Assert(targetResponse.Error?.Message?.Contains("bị hủy") == true, "Thông báo lỗi phải ghi nhận yêu cầu đã bị hủy");

        // Tái sử dụng cùng targetId để chứng minh _inFlightRequests đã được dọn dẹp sạch sẽ
        var reuseResponse = await router.ProcessMessageAsync(BridgeContract.Origin, requestJson);
        Assert(reuseResponse != null && reuseResponse.Success, "Sau khi hủy, cùng ID có thể tái sử dụng mà không bị CONCURRENCY_CONFLICT");
    }

    /// <summary>
    /// Test Case 16: Ranh giới bảo mật Origin
    /// BridgeMessageRouter phải từ chối ngay lập tức và trả về null đối với các nguồn gọi không hợp lệ:
    /// scheme http không an toàn, domain bên ngoài, sai port, origin rỗng hoặc null,
    /// và tuyệt đối không chuyển tiếp tới dispatcher.
    /// </summary>
    public static async Task Test_Router_UntrustedOrigin_RejectedAndReturnsNull()
    {
        bool dispatcherCalled = false;
        var dispatcher = new MockDispatcher((req, ct) =>
        {
            dispatcherCalled = true;
            return Task.FromResult(ResponseEnvelopeHelper.CreateSuccess(req.Id));
        });

        using var router = new BridgeMessageRouter(dispatcher);
        string validJson = $$"""
        {
            "id": "{{Guid.NewGuid()}}",
            "type": "request",
            "command": "settings.get",
            "payload": {},
            "contractVersion": "1.1.0"
        }
        """;

        // 1. Domain bên ngoài
        var res1 = await router.ProcessMessageAsync("https://evil.com", validJson);
        Assert(res1 == null, "Origin bên ngoài 'https://evil.com' phải trả về null");

        // 2. HTTP không bảo mật
        var res2 = await router.ProcessMessageAsync("http://appassets.local", validJson);
        Assert(res2 == null, "Origin không an toàn 'http://appassets.local' phải trả về null");

        // 3. Khác port 443
        var res3 = await router.ProcessMessageAsync("https://appassets.local:8080", validJson);
        Assert(res3 == null, "Origin sai port 'https://appassets.local:8080' phải trả về null");

        // 4. Origin rỗng / whitespace
        var res4 = await router.ProcessMessageAsync("   ", validJson);
        Assert(res4 == null, "Origin rỗng phải trả về null");

        // 5. Origin null
        var res5 = await router.ProcessMessageAsync(null, validJson);
        Assert(res5 == null, "Origin null phải trả về null");

        Assert(!dispatcherCalled, "Dispatcher tuyệt đối không được gọi khi origin không hợp lệ");
    }

    /// <summary>
    /// Test Case 17: Dọn dẹp in-flight registration khi dispatcher gặp ngoại lệ nội bộ
    /// Đảm bảo block finally luôn dọn dẹp CancellationTokenSource và gỡ bỏ ID khỏi danh sách in-flight,
    /// tránh rò rỉ bộ nhớ hoặc gây CONCURRENCY_CONFLICT giả cho các yêu cầu thử lại sau lỗi.
    /// </summary>
    public static async Task Test_Router_InFlightCleanup_OnException_AllowsSubsequentRequestWithSameId()
    {
        int attempt = 0;
        var dispatcher = new MockDispatcher((req, ct) =>
        {
            attempt++;
            if (attempt == 1)
            {
                throw new InvalidOperationException("Mô phỏng lỗi kết nối cơ sở dữ liệu tạm thời");
            }

            return Task.FromResult(ResponseEnvelopeHelper.CreateSuccess(req.Id, new { retrySuccess = true }));
        });

        using var router = new BridgeMessageRouter(dispatcher);
        var reqId = Guid.NewGuid();
        string requestJson = $$"""
        {
            "id": "{{reqId}}",
            "type": "request",
            "command": "settings.get",
            "payload": {},
            "contractVersion": "1.1.0"
        }
        """;

        // Lần 1: Dispatcher ném ngoại lệ
        var res1 = await router.ProcessMessageAsync(BridgeContract.Origin, requestJson);
        Assert(res1 != null && !res1.Success, "Lần gọi 1 phải thất bại do ngoại lệ nội bộ");
        Assert(res1!.Error?.Code == "INTERNAL_ERROR", "Mã lỗi phải là INTERNAL_ERROR");

        // Lần 2: Thử lại với cùng Request ID (idempotent / retry pattern)
        var res2 = await router.ProcessMessageAsync(BridgeContract.Origin, requestJson);
        Assert(res2 != null, "Lần gọi 2 không được null");
        Assert(res2!.Success, "Lần gọi 2 với cùng ID phải thành công và không bị kẹt CONCURRENCY_CONFLICT");
        Assert(res2.Error == null, "Lần gọi 2 không được có lỗi CONCURRENCY_CONFLICT");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
