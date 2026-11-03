using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Jobs;
using Fraylo.Application.Providers;
using Fraylo.Application.Settings;
using Fraylo.Application.Workflows;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;
using Fraylo.Contracts.Settings;
using Fraylo.Contracts.Workflows;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Interceptors;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói kỹ thuật độc lập cho AI Provider Registry, Capabilities, Dispatch và Workflow:
/// - Test 1: Registry đăng ký, tra cứu capability, xử lý provider trùng/không tồn tại.
/// - Test 2: Capabilities thể hiện đúng NOT_VERIFIED và chặn automation khi chưa verify.
/// - Test 3: ProviderDispatchService chặn provider không cấu hình, chặn fallback ngầm giữa Gemini và ChatGPT.
/// - Test 4: WorkflowDefinitionService khởi tạo active version 1, Script luôn requiresReview = true.
/// - Test 5: WorkflowDefinitionService lưu version 2 bất biến, activate và rollback version an toàn.
/// - Test 6: Workflow validation chặn thứ tự bước sai hoặc thiếu bước bắt buộc.
/// </summary>
public static class ProviderRegistryAndWorkflowSmokeTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] {message}");
        }
    }

    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("  Fraylo — Provider Registry, Dispatch & Workflow Smoke Tests    ");
        Console.WriteLine("=================================================================");

        int passed = 0;
        int failed = 0;

        try
        {
            await RunTestAsync("Test 1: Registry đăng ký, tra cứu capability, xử lý provider trùng/không tồn tại", Test_1_ProviderRegistryRegistrationAndLookup);
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED: {ex.Message}");
            failed++;
        }

        try
        {
            await RunTestAsync("Test 2: Capabilities thể hiện đúng NOT_VERIFIED và chặn automation khi chưa verify", Test_2_CapabilitiesNotVerifiedAndAutomationBlocked);
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED: {ex.Message}");
            failed++;
        }

        try
        {
            await RunTestAsync("Test 3: ProviderDispatchService chặn provider không cấu hình, chặn fallback ngầm", Test_3_ProviderDispatchStrictRoutingAndValidation);
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED: {ex.Message}");
            failed++;
        }

        try
        {
            await RunTestAsync("Test 4: WorkflowDefinitionService khởi tạo active version 1, Script luôn requiresReview = true", Test_4_WorkflowDefaultInitialization);
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED: {ex.Message}");
            failed++;
        }

        try
        {
            await RunTestAsync("Test 5: WorkflowDefinitionService lưu version 2 bất biến, activate và rollback an toàn", Test_5_WorkflowVersioningActivationAndRollback);
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED: {ex.Message}");
            failed++;
        }

        try
        {
            await RunTestAsync("Test 6: Workflow validation chặn thứ tự bước sai hoặc thiếu bước bắt buộc", Test_6_WorkflowStepValidation);
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED: {ex.Message}");
            failed++;
        }

        try
        {
            await RunTestAsync("Test 7: Ma trận capability: nhận 2 ảnh, đọc ảnh viết prompt, video không audio (Mốc 3 - T19)", Test_7_CapabilityMatrixDualImageReadingAndNoAudio);
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED: {ex.Message}");
            failed++;
        }

        try
        {
            await RunTestAsync("Test 8: Triển khai Adapters T20a, T20b, T21 & Ranh giới automation chưa sẵn sàng (Mốc 3)", Test_8_ProviderAdaptersExecutionAndAutomationBoundary);
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED: {ex.Message}");
            failed++;
        }

        Console.WriteLine("=================================================================");
        Console.WriteLine($"  Provider Registry & Workflow Tests Finished: {passed} PASSED, {failed} FAILED");
        Console.WriteLine("=================================================================");

        return failed == 0 ? 0 : 1;
    }

    private static async Task RunTestAsync(string testName, Func<Task> testFunc)
    {
        Console.WriteLine($"\n[RUNNING] {testName}...");
        await testFunc();
        Console.WriteLine($"  ✓ {testName} PASSED 100%");
    }

    /// <summary>
    /// Test 1: Registry đăng ký, tra cứu capability, xử lý provider trùng/không tồn tại.
    /// </summary>
    public static Task Test_1_ProviderRegistryRegistrationAndLookup()
    {
        var registry = new ProviderRegistry();

        // 1. Kiểm tra capabilities mặc định cho các provider chuẩn
        var allCaps = registry.GetCapabilities();
        Assert(allCaps.Count > 0, "Capabilities tổng hợp không được rỗng.");
        Assert(allCaps.Any(c => c.ProviderKey == ProviderKeys.ChatGPTWeb), "Phải có capabilities cho chatgpt-web.");
        Assert(allCaps.Any(c => c.ProviderKey == ProviderKeys.GeminiWeb), "Phải có capabilities cho gemini-web.");
        Assert(allCaps.Any(c => c.ProviderKey == ProviderKeys.MuseAI), "Phải có capabilities cho museai.");

        // 2. Tra cứu capability cho từng provider cụ thể
        var chatgptScriptCap = registry.GetCapability(ProviderKeys.ChatGPTWeb, JobTaskType.Script);
        Assert(chatgptScriptCap != null, "chatgpt-web phải có capability cho Script.");
        Assert(chatgptScriptCap!.ProviderKey == ProviderKeys.ChatGPTWeb, "ProviderKey phải là chatgpt-web.");
        Assert(chatgptScriptCap.TaskType == JobTaskType.Script, "TaskType phải là Script.");

        // 3. Tra cứu provider không tồn tại
        var unknownCap = registry.GetCapability("nonexistent-provider", JobTaskType.Script);
        Assert(unknownCap == null, "Tra cứu provider không tồn tại phải trả về null.");

        // 4. Tra cứu task không tồn tại
        var unknownTaskCap = registry.GetCapability(ProviderKeys.ChatGPTWeb, "NonexistentTask");
        Assert(unknownTaskCap == null, "Tra cứu task không tồn tại phải trả về null.");

        // 5. Đăng ký một fake adapter mới
        var fakeAdapter = new FakeTestProviderAdapter("custom-ai");
        registry.RegisterAdapter(fakeAdapter);
        Assert(registry.HasAdapter("custom-ai"), "Registry phải báo HasAdapter = true sau khi đăng ký.");
        Assert(registry.GetAdapter("custom-ai") == fakeAdapter, "GetAdapter phải trả về đúng instance đã đăng ký.");

        // 6. Đăng ký trùng providerKey -> ném InvalidOperationException
        bool threwDuplicate = false;
        try
        {
            registry.RegisterAdapter(new FakeTestProviderAdapter("custom-ai"));
        }
        catch (InvalidOperationException)
        {
            threwDuplicate = true;
        }
        Assert(threwDuplicate, "Đăng ký trùng providerKey phải ném InvalidOperationException.");

        // 7. GetAdapter với provider không tồn tại -> ném KeyNotFoundException
        bool threwNotFound = false;
        try
        {
            registry.GetAdapter("nonexistent-provider");
        }
        catch (KeyNotFoundException)
        {
            threwNotFound = true;
        }
        Assert(threwNotFound, "GetAdapter với provider không tồn tại phải ném KeyNotFoundException.");

        // 8. Đăng ký ghi đè bằng RegisterOrUpdateAdapter -> không ném lỗi
        var updatedFake = new FakeTestProviderAdapter("custom-ai");
        registry.RegisterOrUpdateAdapter(updatedFake);
        Assert(registry.GetAdapter("custom-ai") == updatedFake, "GetAdapter phải trả về adapter đã được cập nhật.");

        // 9. Hủy đăng ký adapter
        bool removed = registry.UnregisterAdapter("custom-ai");
        Assert(removed, "UnregisterAdapter phải trả về true khi xóa thành công.");
        Assert(!registry.HasAdapter("custom-ai"), "HasAdapter phải là false sau khi gỡ.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Test 2: Capabilities thể hiện đúng NOT_VERIFIED và chặn automation khi chưa verify.
    /// </summary>
    public static Task Test_2_CapabilitiesNotVerifiedAndAutomationBlocked()
    {
        var registry = new ProviderRegistry();

        // 1. Kiểm tra chatgpt-web
        var chatgptTasks = new[] { JobTaskType.Script, JobTaskType.Image, JobTaskType.VideoPrompt };
        foreach (var task in chatgptTasks)
        {
            var cap = registry.GetCapability(ProviderKeys.ChatGPTWeb, task);
            Assert(cap != null, $"chatgpt-web phải có capability cho {task}.");
            Assert(cap!.VerificationStatus == ProviderVerificationStatus.NotVerified,
                $"chatgpt-web {task} phải có VerificationStatus là NOT_VERIFIED.");
            Assert(cap.AutomationSupported == false,
                $"chatgpt-web {task} phải có AutomationSupported = false khi chưa xác minh.");
            Assert(cap.ManualSupported == false,
                $"chatgpt-web {task} phải có ManualSupported = false vì chỉ chạy tự động.");

            // Automation check
            Assert(!registry.IsAutomationSupported(ProviderKeys.ChatGPTWeb, task),
                $"IsAutomationSupported phải trả về false cho chatgpt-web {task}.");
        }

        // 2. Kiểm tra gemini-web
        var geminiTasks = new[] { JobTaskType.Script, JobTaskType.Image, JobTaskType.VideoPrompt };
        foreach (var task in geminiTasks)
        {
            var cap = registry.GetCapability(ProviderKeys.GeminiWeb, task);
            Assert(cap != null, $"gemini-web phải có capability cho {task}.");
            Assert(cap!.VerificationStatus == ProviderVerificationStatus.NotVerified,
                $"gemini-web {task} phải có VerificationStatus là NOT_VERIFIED.");
            Assert(cap.AutomationSupported == false,
                $"gemini-web {task} phải có AutomationSupported = false.");
            Assert(cap.ManualSupported == false,
                $"gemini-web {task} phải có ManualSupported = false.");

            Assert(!registry.IsAutomationSupported(ProviderKeys.GeminiWeb, task),
                $"IsAutomationSupported phải trả về false cho gemini-web {task}.");
        }

        // 3. Kiểm tra museai cho VideoRender
        var museCap = registry.GetCapability(ProviderKeys.MuseAI, JobTaskType.VideoRender);
        Assert(museCap != null, "museai phải có capability cho VideoRender.");
        Assert(museCap!.VerificationStatus == ProviderVerificationStatus.NotVerified,
            "museai VideoRender phải là NOT_VERIFIED.");
        Assert(museCap.AutomationSupported == false,
            "museai VideoRender phải có AutomationSupported = false.");
        Assert(museCap.RequiresReferenceImage == true,
            "museai VideoRender yêu cầu RequiresReferenceImage = true.");
        Assert(!registry.IsAutomationSupported(ProviderKeys.MuseAI, JobTaskType.VideoRender),
            "IsAutomationSupported phải trả về false cho museai VideoRender.");

        // 4. Các task không được hỗ trợ
        var unsupportedCap = registry.GetCapability(ProviderKeys.ChatGPTWeb, JobTaskType.VideoRender);
        Assert(unsupportedCap != null, "chatgpt VideoRender capability phải tồn tại.");
        Assert(unsupportedCap!.VerificationStatus == ProviderVerificationStatus.Unsupported,
            "chatgpt VideoRender phải có status UNSUPPORTED.");
        Assert(unsupportedCap.AutomationSupported == false && unsupportedCap.ManualSupported == false,
            "chatgpt VideoRender không hỗ trợ cả automation và manual.");

        // 5. Fake adapter giả mạo AutomationSupported = true nhưng VerificationStatus = NOT_VERIFIED -> vẫn bị chặn!
        var fakeNotVerified = new FakeTestProviderAdapter(
            "unverified-ai",
            new[]
            {
                new ProviderTaskCapabilityDto(
                    "unverified-ai",
                    JobTaskType.Script,
                    ProviderVerificationStatus.NotVerified,
                    AutomationSupported: true, // Cố tình bật cờ automation
                    ManualSupported: false,
                    RequiresReferenceImage: false,
                    MaxInputLength: 4000,
                    EvidenceId: null,
                    VerifiedAtUtc: null,
                    ConfigVersion: 1)
            });
        registry.RegisterAdapter(fakeNotVerified);

        Assert(!registry.IsAutomationSupported("unverified-ai", JobTaskType.Script),
            "Không được phép chạy automation nếu VerificationStatus là NOT_VERIFIED dù AutomationSupported có bật.");

        // 6. Fake adapter được VERIFIED và AutomationSupported = true -> Cho phép automation
        var fakeVerified = new FakeTestProviderAdapter(
            "verified-ai",
            new[]
            {
                new ProviderTaskCapabilityDto(
                    "verified-ai",
                    JobTaskType.Script,
                    ProviderVerificationStatus.Verified,
                    AutomationSupported: true,
                    ManualSupported: false,
                    RequiresReferenceImage: false,
                    MaxInputLength: 4000,
                    EvidenceId: "EV-001",
                    VerifiedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                    ConfigVersion: 1)
            });
        registry.RegisterAdapter(fakeVerified);

        Assert(registry.IsAutomationSupported("verified-ai", JobTaskType.Script),
            "Chỉ khi VERIFIED và AutomationSupported = true thì IsAutomationSupported mới trả về true.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Test 3: ProviderDispatchService chặn provider không cấu hình, chặn fallback ngầm giữa Gemini và ChatGPT.
    /// </summary>
    public static async Task Test_3_ProviderDispatchStrictRoutingAndValidation()
    {
        var (dbOptions, tempDir, _, settingsService) = CreateTestContext("Fraylo_dispatch_test");

        try
        {
            var registry = new ProviderRegistry();
            var dispatchService = new ProviderDispatchService(registry, settingsService, dbOptions);

            // 1. Khi chưa cấu hình provider trong Settings -> ném ProviderNotConfiguredException
            bool threwNotConfigured = false;
            try
            {
                await dispatchService.ResolveProviderKeyForTaskAsync(JobTaskType.Script);
            }
            catch (ProviderNotConfiguredException ex)
            {
                threwNotConfigured = true;
                Assert(ex.TaskType == JobTaskType.Script, "Ngoại lệ phải chứa đúng TaskType.");
            }
            Assert(threwNotConfigured, "ResolveProviderKeyForTaskAsync phải ném ProviderNotConfiguredException khi chưa chọn provider.");

            // 2. Cấu hình ScriptProvider = chatgpt-web, ImageProvider = gemini-web
            await settingsService.SaveSettingsAsync(new SettingsSaveRequest(
                DocumentsPath: Path.Combine(tempDir, "docs"),
                ImagesPath: Path.Combine(tempDir, "images"),
                VideosPath: Path.Combine(tempDir, "videos"),
                ScriptProvider: ProviderKeys.ChatGPTWeb,
                ImageProvider: ProviderKeys.GeminiWeb,
                VideoPromptProvider: ProviderKeys.ChatGPTWeb
            ));

            // 3. Phân giải provider hợp lệ từ Settings
            var resolvedScript = await dispatchService.ResolveProviderKeyForTaskAsync(JobTaskType.Script);
            Assert(resolvedScript == ProviderKeys.ChatGPTWeb, "ScriptProvider phải phân giải thành chatgpt-web.");

            var resolvedImage = await dispatchService.ResolveProviderKeyForTaskAsync(JobTaskType.Image);
            Assert(resolvedImage == ProviderKeys.GeminiWeb, "ImageProvider phải phân giải thành gemini-web.");

            var resolvedVideoRender = await dispatchService.ResolveProviderKeyForTaskAsync(JobTaskType.VideoRender);
            Assert(resolvedVideoRender == ProviderKeys.MuseAI, "VideoRender phải luôn phân giải thành museai.");

            // 4. Chặn đứng fallback ngầm giữa Gemini và ChatGPT
            bool threwMismatch = false;
            try
            {
                await dispatchService.ValidateProviderMatchesConfiguredAsync(JobTaskType.Script, ProviderKeys.GeminiWeb);
            }
            catch (ProviderValidationException ex)
            {
                threwMismatch = true;
                Assert(ex.ErrorCode == "PROVIDER_MISMATCH", "ErrorCode phải là PROVIDER_MISMATCH.");
            }
            Assert(threwMismatch, "Phải ném ngoại lệ khi yêu cầu provider (gemini-web) khác với cấu hình (chatgpt-web).");

            // 5. Chặn gán sai provider (ví dụ dùng MuseAI cho Script)
            bool threwIncompatible = false;
            try
            {
                dispatchService.ValidateDispatch(ProviderKeys.MuseAI, JobTaskType.Script);
            }
            catch (ProviderValidationException ex)
            {
                threwIncompatible = true;
                Assert(ex.ErrorCode == "PROVIDER_CAPABILITY_UNAVAILABLE", "ErrorCode phải là PROVIDER_CAPABILITY_UNAVAILABLE.");
            }
            Assert(threwIncompatible, "Phải chặn museai thực hiện tác vụ Script.");

            // 6. Chặn gán ChatGPT cho VideoRender
            bool threwChatGptVideo = false;
            try
            {
                dispatchService.ValidateDispatch(ProviderKeys.ChatGPTWeb, JobTaskType.VideoRender);
            }
            catch (ProviderValidationException ex)
            {
                threwChatGptVideo = true;
                Assert(ex.ErrorCode == "PROVIDER_CAPABILITY_UNAVAILABLE", "ErrorCode phải là PROVIDER_CAPABILITY_UNAVAILABLE.");
            }
            Assert(threwChatGptVideo, "Phải chặn chatgpt-web thực hiện tác vụ VideoRender.");

            // 7. ValidateAutomationDispatch phải chặn các provider NOT_VERIFIED
            bool threwNotVerified = false;
            try
            {
                dispatchService.ValidateAutomationDispatch(ProviderKeys.ChatGPTWeb, JobTaskType.Script);
            }
            catch (ProviderValidationException ex)
            {
                threwNotVerified = true;
                Assert(ex.ErrorCode == "PROVIDER_NOT_VERIFIED", "ErrorCode phải là PROVIDER_NOT_VERIFIED.");
            }
            Assert(threwNotVerified, "ValidateAutomationDispatch phải ném lỗi khi provider đang ở NOT_VERIFIED.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 4: WorkflowDefinitionService khởi tạo active version 1, Script luôn requiresReview = true.
    /// </summary>
    public static async Task Test_4_WorkflowDefaultInitialization()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext("Fraylo_wf_init_test");

        try
        {
            var workflowService = new WorkflowDefinitionService(dbOptions);

            // 1. Lần đầu truy vấn active workflow -> Tự khởi tạo Version 1
            var activeRes = await workflowService.GetActiveWorkflowAsync();
            Assert(activeRes != null, "Response active workflow không được null.");
            Assert(activeRes!.Revision >= 1, "Revision ban đầu phải >= 1.");

            var def = activeRes.Definition;
            Assert(def.WorkflowId == WorkflowDefinitionService.DefaultWorkflowId, "WorkflowId phải là default.");
            Assert(def.Version == 1, "Phiên bản khởi tạo mặc định phải là Version 1.");
            Assert(def.Steps.Count == 4, "Workflow phải có đúng 4 bước tuyến tính.");

            // 2. Kiểm tra chi tiết 4 bước
            var step1 = def.Steps[0];
            Assert(step1.StepOrder == 1 && step1.TaskType == JobTaskType.Script, "Bước 1 phải là Script.");
            Assert(step1.RequiresReview == true, "Bước 1 (Script) BẮT BUỘC có RequiresReview = true.");

            var step2 = def.Steps[1];
            Assert(step2.StepOrder == 2 && step2.TaskType == JobTaskType.Image, "Bước 2 phải là Image.");

            var step3 = def.Steps[2];
            Assert(step3.StepOrder == 3 && step3.TaskType == JobTaskType.VideoPrompt, "Bước 3 phải là VideoPrompt.");

            var step4 = def.Steps[3];
            Assert(step4.StepOrder == 4 && step4.TaskType == JobTaskType.VideoRender, "Bước 4 phải là VideoRender.");

            // 3. Khởi tạo instance mới cùng DB -> đọc lại đúng Version 1 từ DB đã lưu
            var workflowService2 = new WorkflowDefinitionService(dbOptions);
            var activeRes2 = await workflowService2.GetActiveWorkflowAsync();
            Assert(activeRes2.Definition.Version == 1, "Sau khi restart, active version vẫn là Version 1.");
            Assert(activeRes2.Definition.Steps[0].RequiresReview == true, "Script vẫn giữ RequiresReview = true.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 5: WorkflowDefinitionService lưu version 2 bất biến, activate và rollback version an toàn.
    /// </summary>
    public static async Task Test_5_WorkflowVersioningActivationAndRollback()
    {
        var (dbOptions, tempDir, _, _) = CreateTestContext("Fraylo_wf_version_test");

        try
        {
            var workflowService = new WorkflowDefinitionService(dbOptions);

            // 1. Đảm bảo Version 1 được kích hoạt trước
            var initial = await workflowService.GetActiveWorkflowAsync();
            long initialRevision = initial.Revision;

            // 2. Lưu Version 2 mới: đổi Image thành RequiresReview = true
            var updatedSteps = new List<WorkflowStepDefinitionDto>
            {
                new("step-script", JobTaskType.Script, 1, RequiresReview: true),
                new("step-image", JobTaskType.Image, 2, RequiresReview: true), // Thay đổi RequiresReview cho Image
                new("step-videoprompt", JobTaskType.VideoPrompt, 3, RequiresReview: false),
                new("step-videorender", JobTaskType.VideoRender, 4, RequiresReview: false)
            };

            var saveRequest = new WorkflowSaveVersionRequest(
                WorkflowId: WorkflowDefinitionService.DefaultWorkflowId,
                ExpectedRevision: initialRevision,
                ClientRequestId: Guid.NewGuid().ToString("N"),
                Name: "Pipeline v2 với duyệt ảnh",
                Description: "Quy trình thêm bước duyệt ảnh trước khi sinh video prompt.",
                Steps: updatedSteps
            );

            var saveRes = await workflowService.SaveVersionAsync(saveRequest);
            Assert(saveRes.Definition.Version == 2, "Phiên bản mới lưu phải là Version 2.");
            Assert(saveRes.Revision == initialRevision + 1, "Revision phải tăng thêm 1.");

            // 3. Kiểm tra tính bất biến: Active workflow vẫn phải là Version 1 (vì Version 2 chưa activate)
            var activeCheck = await workflowService.GetActiveWorkflowAsync();
            Assert(activeCheck.Definition.Version == 1, "Active workflow phải giữ nguyên Version 1 khi Version 2 chưa activate.");

            // 4. Kiểm tra Optimistic Concurrency Control: Thử save với ExpectedRevision cũ
            bool threwConcurrency = false;
            try
            {
                await workflowService.SaveVersionAsync(new WorkflowSaveVersionRequest(
                    WorkflowId: WorkflowDefinitionService.DefaultWorkflowId,
                    ExpectedRevision: initialRevision, // Stale revision
                    ClientRequestId: Guid.NewGuid().ToString("N"),
                    Name: "Stale update",
                    Description: "Lỗi",
                    Steps: updatedSteps
                ));
            }
            catch (WorkflowConcurrencyException)
            {
                threwConcurrency = true;
            }
            Assert(threwConcurrency, "SaveVersionAsync phải ném WorkflowConcurrencyException khi ExpectedRevision bị cũ.");

            // 5. Kích hoạt Version 2
            var activateRes = await workflowService.ActivateVersionAsync(new WorkflowActivateRequest(
                WorkflowId: WorkflowDefinitionService.DefaultWorkflowId,
                Version: 2,
                ExpectedRevision: saveRes.Revision,
                ClientRequestId: Guid.NewGuid().ToString("N")
            ));
            Assert(activateRes.Version == 2, "ActivateVersionAsync phải trả về Version = 2.");

            // Kiểm tra active lúc này đã chuyển sang Version 2
            var activeV2 = await workflowService.GetActiveWorkflowAsync();
            Assert(activeV2.Definition.Version == 2, "Active workflow lúc này phải là Version 2.");
            Assert(activeV2.Definition.Steps[1].RequiresReview == true, "Bước 2 (Image) trong Version 2 phải có RequiresReview = true.");

            // 6. Rollback: Kích hoạt lại Version 1
            var rollbackRes = await workflowService.ActivateVersionAsync(new WorkflowActivateRequest(
                WorkflowId: WorkflowDefinitionService.DefaultWorkflowId,
                Version: 1,
                ExpectedRevision: activateRes.Revision,
                ClientRequestId: Guid.NewGuid().ToString("N")
            ));
            Assert(rollbackRes.Version == 1, "Rollback về Version 1 thành công.");

            // Kiểm tra active đã về Version 1
            var activeRollbacked = await workflowService.GetActiveWorkflowAsync();
            Assert(activeRollbacked.Definition.Version == 1, "Active workflow sau rollback phải là Version 1.");
            Assert(activeRollbacked.Definition.Steps[1].RequiresReview == false, "Bước 2 trong Version 1 phải có RequiresReview = false.");

            // 7. Thử kích hoạt version không tồn tại (Version 999)
            bool threwNotFound = false;
            try
            {
                await workflowService.ActivateVersionAsync(new WorkflowActivateRequest(
                    WorkflowId: WorkflowDefinitionService.DefaultWorkflowId,
                    Version: 999,
                    ExpectedRevision: rollbackRes.Revision,
                    ClientRequestId: Guid.NewGuid().ToString("N")
                ));
            }
            catch (WorkflowNotFoundException)
            {
                threwNotFound = true;
            }
            Assert(threwNotFound, "ActivateVersionAsync phải ném WorkflowNotFoundException khi kích hoạt version không tồn tại.");
        }
        finally
        {
            CleanupTestContext(tempDir);
        }
    }

    /// <summary>
    /// Test 6: Workflow validation chặn thứ tự bước sai hoặc thiếu bước bắt buộc.
    /// </summary>
    public static Task Test_6_WorkflowStepValidation()
    {
        // 1. Bị rỗng danh sách
        AssertThrows<WorkflowValidationException>(() =>
            WorkflowDefinitionService.ValidateWorkflowSteps(null),
            "Danh sách null phải bị từ chối.");

        AssertThrows<WorkflowValidationException>(() =>
            WorkflowDefinitionService.ValidateWorkflowSteps(new List<WorkflowStepDefinitionDto>()),
            "Danh sách rỗng phải bị từ chối.");

        // 2. Không đủ 4 bước (chỉ 3 bước)
        var only3Steps = new List<WorkflowStepDefinitionDto>
        {
            new("s1", JobTaskType.Script, 1, RequiresReview: true),
            new("s2", JobTaskType.Image, 2, RequiresReview: false),
            new("s3", JobTaskType.VideoPrompt, 3, RequiresReview: false)
        };
        AssertThrows<WorkflowValidationException>(() =>
            WorkflowDefinitionService.ValidateWorkflowSteps(only3Steps),
            "Workflow chỉ có 3 bước phải bị từ chối.");

        // 3. Bước 1 (Script) có RequiresReview = false -> BẮT BUỘC bị từ chối!
        var scriptWithoutReview = new List<WorkflowStepDefinitionDto>
        {
            new("s1", JobTaskType.Script, 1, RequiresReview: false), // VI PHẠM
            new("s2", JobTaskType.Image, 2, RequiresReview: false),
            new("s3", JobTaskType.VideoPrompt, 3, RequiresReview: false),
            new("s4", JobTaskType.VideoRender, 4, RequiresReview: false)
        };
        AssertThrows<WorkflowValidationException>(() =>
            WorkflowDefinitionService.ValidateWorkflowSteps(scriptWithoutReview),
            "Bước 1 (Script) có RequiresReview = false bắt buộc phải bị từ chối.");

        // 4. Sai loại task ở vị trí (ví dụ vị trí 1 là Image thay vì Script)
        var wrongFirstTask = new List<WorkflowStepDefinitionDto>
        {
            new("s1", JobTaskType.Image, 1, RequiresReview: true), // VI PHẠM
            new("s2", JobTaskType.Script, 2, RequiresReview: false),
            new("s3", JobTaskType.VideoPrompt, 3, RequiresReview: false),
            new("s4", JobTaskType.VideoRender, 4, RequiresReview: false)
        };
        AssertThrows<WorkflowValidationException>(() =>
            WorkflowDefinitionService.ValidateWorkflowSteps(wrongFirstTask),
            "Vị trí bước 1 không phải Script phải bị từ chối.");

        // 5. Sai số thứ tự StepOrder (ví dụ 1, 3, 2, 4)
        var outOfOrderSteps = new List<WorkflowStepDefinitionDto>
        {
            new("s1", JobTaskType.Script, 1, RequiresReview: true),
            new("s2", JobTaskType.Image, 3, RequiresReview: false), // VI PHẠM
            new("s3", JobTaskType.VideoPrompt, 2, RequiresReview: false),
            new("s4", JobTaskType.VideoRender, 4, RequiresReview: false)
        };
        AssertThrows<WorkflowValidationException>(() =>
            WorkflowDefinitionService.ValidateWorkflowSteps(outOfOrderSteps),
            "StepOrder không tuần tự 1..4 phải bị từ chối.");

        // 6. Trùng lặp StepKey
        var duplicateKeys = new List<WorkflowStepDefinitionDto>
        {
            new("step-common", JobTaskType.Script, 1, RequiresReview: true),
            new("step-common", JobTaskType.Image, 2, RequiresReview: false), // VI PHẠM
            new("step-videoprompt", JobTaskType.VideoPrompt, 3, RequiresReview: false),
            new("step-videorender", JobTaskType.VideoRender, 4, RequiresReview: false)
        };
        AssertThrows<WorkflowValidationException>(() =>
            WorkflowDefinitionService.ValidateWorkflowSteps(duplicateKeys),
            "StepKey trùng lặp phải bị từ chối.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Test 7: Ma trận capability: nhận 2 ảnh, đọc ảnh viết prompt, video không audio (Mốc 3 - T19)
    /// </summary>
    public static Task Test_7_CapabilityMatrixDualImageReadingAndNoAudio()
    {
        var registry = new ProviderRegistry();

        // 1. ChatGPT Web
        var chatgptImageCap = registry.GetCapability(ProviderKeys.ChatGPTWeb, JobTaskType.Image);
        Assert(chatgptImageCap != null, "ChatGPT Web phải có capability cho Image.");
        Assert(chatgptImageCap!.SupportsDualReferenceImages, "ChatGPT Web Image phải có cờ SupportsDualReferenceImages = true.");
        Assert(chatgptImageCap.VerificationStatus == ProviderVerificationStatus.NotVerified, "ChatGPT Web Image phải ở trạng thái NOT_VERIFIED khi chưa qua live verification.");
        Assert(!chatgptImageCap.AutomationSupported, "ChatGPT Web Image AutomationSupported phải là false khi chưa verify.");
        Assert(!chatgptImageCap.ManualSupported, "ChatGPT Web Image không cung cấp Manual Handoff.");

        var chatgptPromptCap = registry.GetCapability(ProviderKeys.ChatGPTWeb, JobTaskType.VideoPrompt);
        Assert(chatgptPromptCap != null, "ChatGPT Web phải có capability cho VideoPrompt.");
        Assert(chatgptPromptCap!.SupportsImageInputForPrompt, "ChatGPT Web VideoPrompt phải có cờ SupportsImageInputForPrompt = true.");

        var chatgptVideoCap = registry.GetCapability(ProviderKeys.ChatGPTWeb, JobTaskType.VideoRender);
        Assert(chatgptVideoCap != null && chatgptVideoCap.VerificationStatus == ProviderVerificationStatus.Unsupported,
            "ChatGPT Web VideoRender phải là UNSUPPORTED.");

        // 2. Gemini Web
        var geminiImageCap = registry.GetCapability(ProviderKeys.GeminiWeb, JobTaskType.Image);
        Assert(geminiImageCap != null, "Gemini Web phải có capability cho Image.");
        Assert(geminiImageCap!.SupportsDualReferenceImages, "Gemini Web Image phải có cờ SupportsDualReferenceImages = true.");
        Assert(geminiImageCap.VerificationStatus == ProviderVerificationStatus.NotVerified, "Gemini Web Image phải là NOT_VERIFIED.");

        var geminiPromptCap = registry.GetCapability(ProviderKeys.GeminiWeb, JobTaskType.VideoPrompt);
        Assert(geminiPromptCap != null, "Gemini Web phải có capability cho VideoPrompt.");
        Assert(geminiPromptCap!.SupportsImageInputForPrompt, "Gemini Web VideoPrompt phải có cờ SupportsImageInputForPrompt = true.");

        var geminiVideoCap = registry.GetCapability(ProviderKeys.GeminiWeb, JobTaskType.VideoRender);
        Assert(geminiVideoCap != null && geminiVideoCap.VerificationStatus == ProviderVerificationStatus.Unsupported,
            "Gemini Web VideoRender phải là UNSUPPORTED.");

        // 3. MuseAI
        var museVideoCap = registry.GetCapability(ProviderKeys.MuseAI, JobTaskType.VideoRender);
        Assert(museVideoCap != null, "MuseAI phải có capability cho VideoRender.");
        Assert(museVideoCap!.SupportsNoAudioVideo, "MuseAI VideoRender phải có cờ SupportsNoAudioVideo = true.");
        Assert(!string.IsNullOrWhiteSpace(museVideoCap.AudioBehaviorDescription), "MuseAI VideoRender phải có AudioBehaviorDescription giải thích rõ ràng.");
        Assert(museVideoCap.VerificationStatus == ProviderVerificationStatus.NotVerified, "MuseAI VideoRender phải là NOT_VERIFIED.");

        var museScriptCap = registry.GetCapability(ProviderKeys.MuseAI, JobTaskType.Script);
        Assert(museScriptCap != null && museScriptCap.VerificationStatus == ProviderVerificationStatus.Unsupported,
            "MuseAI Script phải là UNSUPPORTED.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Test 8: Triển khai Adapters T20a, T20b, T21 & Ranh giới automation chưa sẵn sàng (Mốc 3)
    /// </summary>
    public static async Task Test_8_ProviderAdaptersExecutionAndAutomationBoundary()
    {
        var chatgptAdapter = new ChatGptWebAdapter();
        var geminiAdapter = new GeminiWebAdapter();
        var museAdapter = new MuseAiVideoAdapter();

        Assert(chatgptAdapter.ProviderKey == ProviderKeys.ChatGPTWeb, "ChatGPT adapter key phải đúng.");
        Assert(geminiAdapter.ProviderKey == ProviderKeys.GeminiWeb, "Gemini adapter key phải đúng.");
        Assert(museAdapter.ProviderKey == ProviderKeys.MuseAI, "MuseAI adapter key phải đúng.");

        var contextImage = new JobExecutionContext(
            JobId: "job-img-01",
            ProjectId: "proj-01",
            SceneId: "scene-01",
            TaskType: JobTaskType.Image,
            ProviderKey: ProviderKeys.ChatGPTWeb,
            StorageRootId: "root-01",
            PromptSnapshotText: "Tạo ảnh người mẫu mặc áo sơ mi",
            ConfigSnapshotJson: "{}",
            FencingToken: 1,
            Attempts: 1,
            ExistingResultReference: null,
            ExistingResultText: null
        );

        bool sideEffectCommitted = false;
        Task CommitSideEffectHook()
        {
            sideEffectCommitted = true;
            return Task.CompletedTask;
        }

        // 1. Thử execute với ChatGPT khi chưa verify: Phải từ chối automation ngầm, không đoán selector
        var resultChatgpt = await chatgptAdapter.ExecuteAsync(contextImage, CommitSideEffectHook, null);
        Assert(!resultChatgpt.Success, "Execute ChatGPT chưa verify phải trả Success = false.");
        Assert(resultChatgpt.ReasonCode == "automation_blocked", "ReasonCode phải là automation_blocked.");
        Assert(!resultChatgpt.SideEffectSubmitted, "SideEffectSubmitted phải là false để bảo toàn database.");
        Assert(!sideEffectCommitted, "Hook commit side effect không được gọi khi chưa submit.");

        var resultGemini = await geminiAdapter.ExecuteAsync(contextImage with { ProviderKey = ProviderKeys.GeminiWeb }, CommitSideEffectHook, null);
        Assert(!resultGemini.Success && resultGemini.ReasonCode == "automation_blocked" && !sideEffectCommitted,
            "Gemini chưa sẵn sàng phải chặn automation trước submit, không yêu cầu handoff.");

        // 2. Thử execute với MuseAI cho VideoRender khi chưa verify: Phải từ chối automation ngầm
        var contextVideo = new JobExecutionContext(
            JobId: "job-vid-01",
            ProjectId: "proj-01",
            SceneId: "scene-01",
            TaskType: JobTaskType.VideoRender,
            ProviderKey: ProviderKeys.MuseAI,
            StorageRootId: "root-01",
            PromptSnapshotText: "Camera lia 45 độ",
            ConfigSnapshotJson: "{}",
            FencingToken: 1,
            Attempts: 1,
            ExistingResultReference: null,
            ExistingResultText: null
        );

        var resultMuse = await museAdapter.ExecuteAsync(contextVideo, CommitSideEffectHook, null);
        Assert(!resultMuse.Success, "Execute MuseAI chưa verify phải trả Success = false.");
        Assert(resultMuse.ReasonCode == "automation_blocked", "ReasonCode phải là automation_blocked.");
        Assert(!resultMuse.SideEffectSubmitted, "SideEffectSubmitted phải là false.");

        // 3. MuseAI cho Script: Unsupported
        var contextUnsupported = new JobExecutionContext(
            JobId: "job-unsupported",
            ProjectId: "proj-01",
            SceneId: "scene-01",
            TaskType: JobTaskType.Script,
            ProviderKey: ProviderKeys.MuseAI,
            StorageRootId: "root-01",
            PromptSnapshotText: "Viết kịch bản",
            ConfigSnapshotJson: "{}",
            FencingToken: 1,
            Attempts: 1,
            ExistingResultReference: null,
            ExistingResultText: null
        );
        var resultUnsupported = await museAdapter.ExecuteAsync(contextUnsupported, CommitSideEffectHook, null);
        Assert(!resultUnsupported.Success, "MuseAI Script phải trả Success = false.");
        Assert(resultUnsupported.ReasonCode == "CAPABILITY_UNSUPPORTED", "ReasonCode phải là CAPABILITY_UNSUPPORTED.");

        // 4. DeterministicFakeProviderAdapter dành cho kiểm thử điều phối: Phải gọi commit hook và trả kết quả xác định
        var fakeAdapter = new DeterministicFakeProviderAdapter(ProviderKeys.ChatGPTWeb);
        bool fakeCommitCalled = false;
        var fakeResult = await fakeAdapter.ExecuteAsync(contextImage, () => { fakeCommitCalled = true; return Task.CompletedTask; }, null);
        Assert(fakeResult.Success, "Fake adapter phải trả Success = true cho test.");
        Assert(fakeResult.SideEffectSubmitted, "Fake adapter phải đánh dấu SideEffectSubmitted = true.");
        Assert(fakeCommitCalled, "Fake adapter phải gọi onPreSideEffectCommitAsync theo đúng chuẩn T16.");
        Assert(fakeResult.ResultReference != null && fakeResult.ResultReference.Contains("generated-image"), "Fake adapter phải trả ResultReference ảnh hợp lệ.");

        var registry = new ProviderRegistry();
        registry.RegisterAdapter(fakeAdapter);
        var executor = new ProductionJobExecutor(registry);
        fakeCommitCalled = false;
        var routed = await executor.ExecuteAsync(contextImage, () => { fakeCommitCalled = true; return Task.CompletedTask; }, null, CancellationToken.None);
        Assert(routed.Success && fakeCommitCalled, "Capability đã xác minh phải tiếp tục chạy qua executor tự động.");

        registry.RegisterAdapter(museAdapter);
        var unsupported = await executor.ExecuteAsync(contextUnsupported, CommitSideEffectHook, null, CancellationToken.None);
        Assert(!unsupported.Success && unsupported.ReasonCode == "CAPABILITY_UNSUPPORTED",
            "Executor phải từ chối capability không hỗ trợ trước khi gọi adapter.");
    }

    private static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        bool threw = false;
        try
        {
            action();
        }
        catch (TException)
        {
            threw = true;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Kỳ vọng ném {typeof(TException).Name} nhưng nhận được {ex.GetType().Name}: {ex.Message}");
        }

        if (!threw)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] {message}");
        }
    }

    private static (DbContextOptions<FrayloDbContext> Options, string TempDir, StorageRootRegistry Registry, SettingsService Settings) CreateTestContext(string prefix)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "test.db");
        var connectionString = $"Data Source={dbPath}";

        var optionsBuilder = new DbContextOptionsBuilder<FrayloDbContext>();
        optionsBuilder.UseSqlite(connectionString)
                      .AddInterceptors(new SqlitePragmaInterceptor())
                      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        var dbOptions = optionsBuilder.Options;

        using (var db = new FrayloDbContext(dbOptions))
        {
            db.Database.EnsureCreated();
        }

        var registry = new StorageRootRegistry(dbOptions, tempDir);
        registry.EnsureDefaultRootsAsync().GetAwaiter().GetResult();
        var settingsService = new SettingsService(dbOptions, registry);

        return (dbOptions, tempDir, registry, settingsService);
    }

    private static void CleanupTestContext(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch { }
    }

    /// <summary>
    /// Fake adapter dùng riêng cho việc kiểm thử tính linh hoạt của ProviderRegistry.
    /// </summary>
    private sealed class FakeTestProviderAdapter : IProviderAdapter
    {
        public string ProviderKey { get; }
        private readonly IReadOnlyList<ProviderTaskCapabilityDto> _capabilities;

        public FakeTestProviderAdapter(string providerKey, IReadOnlyList<ProviderTaskCapabilityDto>? capabilities = null)
        {
            ProviderKey = providerKey;
            _capabilities = capabilities ?? new[]
            {
                new ProviderTaskCapabilityDto(
                    providerKey,
                    JobTaskType.Script,
                    ProviderVerificationStatus.NotVerified,
                    AutomationSupported: false,
                    ManualSupported: false,
                    RequiresReferenceImage: false,
                    MaxInputLength: 4000,
                    EvidenceId: null,
                    VerifiedAtUtc: null,
                    ConfigVersion: 1)
            };
        }

        public IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities() => _capabilities;

        public Task<ProviderTaskCapabilityDto?> GetCapabilityForTaskAsync(string taskType, CancellationToken ct = default)
        {
            var cap = _capabilities.FirstOrDefault(c => string.Equals(c.TaskType, taskType, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(cap);
        }

        public Task<JobExecutionResult> ExecuteAsync(
            JobExecutionContext context,
            Func<Task> onPreSideEffectCommitAsync,
            Func<int, string, Task>? onProgressAsync,
            CancellationToken ct = default)
        {
            return Task.FromResult(new JobExecutionResult(
                Success: true,
                ResultReference: "test-ref",
                ResultText: "test-result",
                SideEffectSubmitted: true
            ));
        }
    }
}
