using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Browser;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Browser;
using Fraylo.Desktop.Bridge;
using Fraylo.Desktop.Bridge.Handlers;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói (Smoke Tests) cho Track R1:
/// Desktop Browser Handlers & Bridge Integration.
/// Kiểm thử toàn diện qua BridgeDispatcher:
/// - browser.list_profiles (phạm vi toàn ứng dụng)
/// - browser.request_login (VisibleActive slot đầu tiên, VisibleWaiting queue)
/// - browser.cancel_login (giải phóng slot hiển thị, cập nhật trạng thái Hidden)
/// - browser.check_session (trạng thái thực Unverified / Connected, không trả fake PASS)
/// - UNKNOWN_COMMAND cho lệnh ngoài allow-list
/// - INVALID_ARGUMENT cho payload null hoặc sai/thiếu trường
/// - ENTITY_NOT_FOUND cho profile không tồn tại
/// - Unregister dọn dẹp handler
/// </summary>
public static class BrowserBridgeSmokeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static async Task<(SqliteConnection Connection, Func<FrayloDbContext> Factory)> CreateInMemoryDbContextFactoryAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<FrayloDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var initialDb = new FrayloDbContext(options))
        {
            await initialDb.Database.EnsureCreatedAsync();
        }

        return (connection, () => new FrayloDbContext(options));
    }

    /// <summary>
    /// Test 1: browser.list_profiles trả về profile toàn ứng dụng.
    /// </summary>
    public static async Task Test_ListProfiles_GlobalScope()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_bridge_lp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                using var coordinator = new LoginCoordinator(registry);
                var probe = new SessionProbe(registry);
                var handlers = new BrowserHandlers(registry, coordinator, probe);

                var dispatcher = new BridgeDispatcher();
                handlers.Register(dispatcher);

                await registry.EnsureProfileAsync("shared_ai", BrowserProfileKind.SharedAI, accountIdentity: null);
                await registry.EnsureProfileAsync("museai_alpha", BrowserProfileKind.MuseAI, accountIdentity: "alpha@tito.local");
                await registry.EnsureProfileAsync("museai_beta", BrowserProfileKind.MuseAI, accountIdentity: "beta@tito.local");

                // Browser profiles là tài nguyên cấp ứng dụng.
                var reqAll = CreateRequest("browser.list_profiles", new BrowserListProfilesRequest());
                var resAll = await dispatcher.DispatchAsync(reqAll, CancellationToken.None);

                Assert(resAll.Success, "browser.list_profiles phải thành công");
                var dataAll = DeserializeData<BrowserListProfilesResponseData>(resAll);
                Assert(dataAll.Profiles.Count == 3, $"Kỳ vọng 3 profile nhưng nhận được {dataAll.Profiles.Count}");

                // Kiểm tra chi tiết ánh xạ DTO của profile SharedAI
                var sharedProfile = dataAll.Profiles.FirstOrDefault(p => p.ProfileId == "shared_ai");
                Assert(sharedProfile != null, "Profile shared_ai phải có trong kết quả trả về");
                Assert(sharedProfile!.Kind == BrowserProfileKind.SharedAI, "Kind phải là SharedAI");
                Assert(sharedProfile.VisibilityState == BrowserVisibilityState.Hidden, "VisibilityState mặc định phải là Hidden");
                Assert(sharedProfile.SessionStatus == BrowserSessionStatus.Unverified, "SessionStatus mặc định phải là Unverified");
                Assert(sharedProfile.OperationalState == BrowserOperationalState.Stopped, "OperationalState mặc định phải là Stopped");
                Assert(sharedProfile.ActiveTabs != null, "ActiveTabs không được null");

                // Danh sách luôn hiển thị toàn bộ profile đã đăng ký.
                var reqAlpha = CreateRequest("browser.list_profiles", new BrowserListProfilesRequest());
                var resAlpha = await dispatcher.DispatchAsync(reqAlpha, CancellationToken.None);

                Assert(resAlpha.Success, "browser.list_profiles phải thành công");
                var dataAlpha = DeserializeData<BrowserListProfilesResponseData>(resAlpha);
                Assert(dataAlpha.Profiles.Count == 3, $"Kỳ vọng 3 profile toàn ứng dụng nhưng nhận được {dataAlpha.Profiles.Count}");
                Assert(dataAlpha.Profiles.Any(p => p.ProfileId == "shared_ai"), "Kết quả phải chứa shared_ai");
                Assert(dataAlpha.Profiles.Any(p => p.ProfileId == "museai_alpha"), "Kết quả phải chứa museai_alpha");
                Assert(dataAlpha.Profiles.Any(p => p.ProfileId == "museai_beta"), "Kết quả phải chứa mọi provider profile toàn ứng dụng.");

                // Payload null vẫn trả về toàn bộ profile.
                var reqNullPayload = CreateRequestWithRawJson("browser.list_profiles", "null");
                var resNullPayload = await dispatcher.DispatchAsync(reqNullPayload, CancellationToken.None);
                Assert(resNullPayload.Success, "browser.list_profiles với payload null phải thành công");
                var dataNullPayload = DeserializeData<BrowserListProfilesResponseData>(resNullPayload);
                Assert(dataNullPayload.Profiles.Count == 3, "Payload null phải trả về toàn bộ danh sách profile");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 2: browser.request_login: cho phép nhiều profile khác nhau mở đồng thời VisibleActive, không bị xếp hàng.
    /// </summary>
    public static async Task Test_RequestLogin_ConcurrentLogins_IndependentVisibleActive()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_bridge_rl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                using var coordinator = new LoginCoordinator(registry);
                var probe = new SessionProbe(registry);
                var handlers = new BrowserHandlers(registry, coordinator, probe);

                var dispatcher = new BridgeDispatcher();
                handlers.Register(dispatcher);


                await registry.EnsureProfileAsync("prof_first", BrowserProfileKind.SharedAI);
                await registry.EnsureProfileAsync("prof_second", BrowserProfileKind.MuseAI);

                // Profile đầu tiên yêu cầu login: nhận ngay VisibleActive
                var reqFirst = CreateRequest("browser.request_login", new BrowserRequestLoginRequest("prof_first"));
                var resFirst = await dispatcher.DispatchAsync(reqFirst, CancellationToken.None);

                Assert(resFirst.Success, "Yêu cầu đăng nhập profile đầu tiên phải thành công");
                var dataFirst = DeserializeData<BrowserRequestLoginResponseData>(resFirst);
                Assert(!dataFirst.Queued, "Profile đầu tiên không được ở trạng thái queued");
                Assert(dataFirst.VisibilityState == BrowserVisibilityState.VisibleActive, "VisibilityState phải là VisibleActive");
                Assert(dataFirst.CurrentHolder == "prof_first", "CurrentHolder phải là prof_first");

                // Kiểm tra trạng thái đã được ghi bền vững vào ProfileRegistry
                var storedFirst = await registry.GetProfileAsync("prof_first");
                Assert(storedFirst?.VisibilityState == BrowserVisibilityState.VisibleActive, "Trạng thái trong registry phải là VisibleActive");

                // Profile thứ hai yêu cầu login đồng thời: nhận ngay VisibleActive, không bị queued
                var reqSecond = CreateRequest("browser.request_login", new BrowserRequestLoginRequest("prof_second"));
                var resSecond = await dispatcher.DispatchAsync(reqSecond, CancellationToken.None);

                Assert(resSecond.Success, "Yêu cầu đăng nhập profile thứ hai phải thành công");
                var dataSecond = DeserializeData<BrowserRequestLoginResponseData>(resSecond);
                Assert(!dataSecond.Queued, "Profile thứ hai không bị xếp hàng (Queued == false)");
                Assert(dataSecond.VisibilityState == BrowserVisibilityState.VisibleActive, "VisibilityState phải là VisibleActive");
                Assert(dataSecond.CurrentHolder == "prof_second", "CurrentHolder phải là prof_second");

                // Kiểm tra trạng thái của cả hai profile trong registry: cả hai đều VisibleActive đồng thời
                var storedSecond = await registry.GetProfileAsync("prof_second");
                Assert(storedSecond?.VisibilityState == BrowserVisibilityState.VisibleActive, "Trạng thái prof_second trong registry phải là VisibleActive");
                Assert(coordinator.ActiveVisibleProfileIds.Count == 2, "Coordinator phải theo dõi 2 profile đang visible đồng thời");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 3: browser.cancel_login: đóng cửa sổ của profile chỉ định, profile còn lại vẫn giữ VisibleActive độc lập.
    /// </summary>
    public static async Task Test_CancelLogin_ReleasesSpecificProfileSlot_OtherProfileRemainsActive()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_bridge_cl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                using var coordinator = new LoginCoordinator(registry);
                var probe = new SessionProbe(registry);
                var handlers = new BrowserHandlers(registry, coordinator, probe);

                var dispatcher = new BridgeDispatcher();
                handlers.Register(dispatcher);


                await registry.EnsureProfileAsync("prof_active", BrowserProfileKind.SharedAI);
                await registry.EnsureProfileAsync("prof_active2", BrowserProfileKind.MuseAI);

                // Mở cửa sổ login đồng thời cho cả prof_active và prof_active2
                await dispatcher.DispatchAsync(CreateRequest("browser.request_login", new BrowserRequestLoginRequest("prof_active")), CancellationToken.None);
                await dispatcher.DispatchAsync(CreateRequest("browser.request_login", new BrowserRequestLoginRequest("prof_active2")), CancellationToken.None);

                Assert(coordinator.ActiveVisibleProfileIds.Count == 2, "Cả 2 profile đều đang VisibleActive");

                // Hủy đăng nhập prof_active
                var reqCancelActive = CreateRequest("browser.cancel_login", new BrowserCancelLoginRequest("prof_active"));
                var resCancelActive = await dispatcher.DispatchAsync(reqCancelActive, CancellationToken.None);

                Assert(resCancelActive.Success, "Hủy đăng nhập prof_active phải thành công");
                var dataCancelActive = DeserializeData<BrowserCancelLoginResponseData>(resCancelActive);
                Assert(dataCancelActive.Canceled, "Canceled phải là true");

                // Kiểm tra trạng thái prof_active trong registry đã chuyển về Hidden / Stopped
                var storedActive = await registry.GetProfileAsync("prof_active");
                Assert(storedActive?.VisibilityState == BrowserVisibilityState.Hidden, "Trạng thái prof_active phải là Hidden sau khi hủy");
                Assert(storedActive?.OperationalState == BrowserOperationalState.Stopped, "Trạng thái vận hành phải là Stopped");

                // Profile thứ hai (prof_active2) VẪN PHẢI GIỮ NGUYÊN VisibleActive!
                var storedActive2 = await registry.GetProfileAsync("prof_active2");
                Assert(storedActive2?.VisibilityState == BrowserVisibilityState.VisibleActive, "prof_active2 vẫn phải là VisibleActive");
                Assert(coordinator.IsProfileVisible("prof_active2"), "prof_active2 vẫn phải visible trong coordinator");

                // Hủy nốt prof_active2
                var reqCancelActive2 = CreateRequest("browser.cancel_login", new BrowserCancelLoginRequest("prof_active2"));
                var resCancelActive2 = await dispatcher.DispatchAsync(reqCancelActive2, CancellationToken.None);

                Assert(resCancelActive2.Success, "Hủy đăng nhập prof_active2 phải thành công");
                var storedActive2After = await registry.GetProfileAsync("prof_active2");
                Assert(storedActive2After?.VisibilityState == BrowserVisibilityState.Hidden, "Trạng thái prof_active2 phải là Hidden sau khi hủy");
                Assert(!coordinator.IsVisibleLoginActive, "Coordinator không còn cửa sổ active nào");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 4: browser.check_session: trả về trạng thái session thật (Unverified / Connected), không trả kết quả giả.
    /// </summary>
    public static async Task Test_CheckSession_RealStatus_UnverifiedAndConnected_NoFakePass()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_bridge_cs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                using var coordinator = new LoginCoordinator(registry);
                var probe = new SessionProbe(registry);
                var handlers = new BrowserHandlers(registry, coordinator, probe);

                var dispatcher = new BridgeDispatcher();
                handlers.Register(dispatcher);


                await registry.EnsureProfileAsync("prof_unverified", BrowserProfileKind.SharedAI);
                await registry.EnsureProfileAsync("prof_connected", BrowserProfileKind.MuseAI, accountIdentity: "user@tito.local");
                await registry.UpdateProfileStateAsync("prof_connected", sessionStatus: BrowserSessionStatus.Connected);

                // Kiểm tra profile chưa xác minh
                var reqUnverified = CreateRequest("browser.check_session", new BrowserCheckSessionRequest("prof_unverified"));
                var resUnverified = await dispatcher.DispatchAsync(reqUnverified, CancellationToken.None);

                Assert(resUnverified.Success, "check_session trên prof_unverified phải thành công");
                var dataUnverified = DeserializeData<BrowserCheckSessionResponseData>(resUnverified);
                Assert(dataUnverified.SessionStatus == BrowserSessionStatus.Unverified, "SessionStatus phải là Unverified");
                Assert(dataUnverified.IdentityMatches == null, "IdentityMatches phải là null khi chưa liên kết danh tính");

                // Kiểm tra profile đã kết nối thực
                var reqConnected = CreateRequest("browser.check_session", new BrowserCheckSessionRequest("prof_connected"));
                var resConnected = await dispatcher.DispatchAsync(reqConnected, CancellationToken.None);

                Assert(resConnected.Success, "check_session trên prof_connected phải thành công");
                var dataConnected = DeserializeData<BrowserCheckSessionResponseData>(resConnected);
                Assert(dataConnected.SessionStatus == BrowserSessionStatus.Connected, "SessionStatus phải là Connected");
                Assert(dataConnected.IdentityMatches == true, "IdentityMatches phải là true khi danh tính trùng khớp");

                // Profile không tồn tại trong hệ thống: trả về trạng thái Unverified (không trả PASS giả)
                var reqNonExistent = CreateRequest("browser.check_session", new BrowserCheckSessionRequest("prof_non_existent"));
                var resNonExistent = await dispatcher.DispatchAsync(reqNonExistent, CancellationToken.None);

                Assert(resNonExistent.Success, "check_session trên profile không tồn tại vẫn trả về envelope thành công chứa Unverified");
                var dataNonExistent = DeserializeData<BrowserCheckSessionResponseData>(resNonExistent);
                Assert(dataNonExistent.SessionStatus == BrowserSessionStatus.Unverified, "SessionStatus cho profile không tồn tại phải là Unverified");
                Assert(dataNonExistent.IdentityMatches == null, "IdentityMatches cho profile không tồn tại phải là null");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 5: Lệnh không nằm trong Allow-list trả về UNKNOWN_COMMAND.
    /// </summary>
    public static async Task Test_UnknownCommand_RejectedWithUnknownCommand()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_bridge_uc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                using var coordinator = new LoginCoordinator(registry);
                var probe = new SessionProbe(registry);
                var handlers = new BrowserHandlers(registry, coordinator, probe);

                var dispatcher = new BridgeDispatcher();
                handlers.Register(dispatcher);

                var reqUnknown = CreateRequest("browser.launch_stealth", new { profileId = "shared_ai" });
                var resUnknown = await dispatcher.DispatchAsync(reqUnknown, CancellationToken.None);

                Assert(!resUnknown.Success, "Lệnh không hợp lệ phải trả về Success = false");
                Assert(resUnknown.Error?.Code == "UNKNOWN_COMMAND", $"Mã lỗi phải là UNKNOWN_COMMAND, nhận được: {resUnknown.Error?.Code}");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 6: Payload sai/thiếu trường hoặc định dạng không hợp lệ trả về INVALID_ARGUMENT.
    /// </summary>
    public static async Task Test_PayloadValidation_InvalidArguments_Rejected()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_bridge_val_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                using var coordinator = new LoginCoordinator(registry);
                var probe = new SessionProbe(registry);
                var handlers = new BrowserHandlers(registry, coordinator, probe);

                var dispatcher = new BridgeDispatcher();
                handlers.Register(dispatcher);

                // 1. request_login thiếu profileId
                var reqMissing1 = CreateRequestWithRawJson("browser.request_login", "{}");
                var resMissing1 = await dispatcher.DispatchAsync(reqMissing1, CancellationToken.None);
                Assert(!resMissing1.Success, "request_login thiếu profileId phải thất bại");
                Assert(resMissing1.Error?.Code == "INVALID_ARGUMENT", "Mã lỗi phải là INVALID_ARGUMENT");

                // 2. request_login với profileId rỗng
                var reqEmpty1 = CreateRequest("browser.request_login", new { profileId = "   " });
                var resEmpty1 = await dispatcher.DispatchAsync(reqEmpty1, CancellationToken.None);
                Assert(!resEmpty1.Success, "request_login với profileId khoảng trắng phải thất bại");
                Assert(resEmpty1.Error?.Code == "INVALID_ARGUMENT", "Mã lỗi phải là INVALID_ARGUMENT");

                // 3. cancel_login thiếu profileId (payload null)
                var reqNullCancel = CreateRequestWithRawJson("browser.cancel_login", "null");
                var resNullCancel = await dispatcher.DispatchAsync(reqNullCancel, CancellationToken.None);
                Assert(!resNullCancel.Success, "cancel_login với payload null phải thất bại");
                Assert(resNullCancel.Error?.Code == "INVALID_ARGUMENT", "Mã lỗi phải là INVALID_ARGUMENT");

                // 4. check_session với profileId rỗng
                var reqEmptyCs = CreateRequest("browser.check_session", new { profileId = "" });
                var resEmptyCs = await dispatcher.DispatchAsync(reqEmptyCs, CancellationToken.None);
                Assert(!resEmptyCs.Success, "check_session với profileId rỗng phải thất bại");
                Assert(resEmptyCs.Error?.Code == "INVALID_ARGUMENT", "Mã lỗi phải là INVALID_ARGUMENT");

                // 5. list_profiles với JSON không phải object (ví dụ số nguyên hoặc chuỗi trần)
                var reqMalformed = CreateRequestWithRawJson("browser.list_profiles", "12345");
                var resMalformed = await dispatcher.DispatchAsync(reqMalformed, CancellationToken.None);
                Assert(!resMalformed.Success, "list_profiles với payload số nguyên phải thất bại");
                Assert(resMalformed.Error?.Code == "INVALID_ARGUMENT", "Mã lỗi phải là INVALID_ARGUMENT");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 7: Yêu cầu login hoặc hủy login trên profile không tồn tại trả về ENTITY_NOT_FOUND.
    /// </summary>
    public static async Task Test_EntityNotFound_ForNonExistentProfile_OnLoginAndCancel()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_bridge_enf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                using var coordinator = new LoginCoordinator(registry);
                var probe = new SessionProbe(registry);
                var handlers = new BrowserHandlers(registry, coordinator, probe);

                var dispatcher = new BridgeDispatcher();
                handlers.Register(dispatcher);

                // request_login với profile không tồn tại -> ENTITY_NOT_FOUND
                var reqLoginNotFound = CreateRequest("browser.request_login", new BrowserRequestLoginRequest("ghost_profile_123"));
                var resLoginNotFound = await dispatcher.DispatchAsync(reqLoginNotFound, CancellationToken.None);
                Assert(!resLoginNotFound.Success, "request_login với profile không tồn tại phải thất bại");
                Assert(resLoginNotFound.Error?.Code == "ENTITY_NOT_FOUND", $"Mã lỗi phải là ENTITY_NOT_FOUND, nhận được: {resLoginNotFound.Error?.Code}");

                // cancel_login với profile không tồn tại -> ENTITY_NOT_FOUND
                var reqCancelNotFound = CreateRequest("browser.cancel_login", new BrowserCancelLoginRequest("ghost_profile_123"));
                var resCancelNotFound = await dispatcher.DispatchAsync(reqCancelNotFound, CancellationToken.None);
                Assert(!resCancelNotFound.Success, "cancel_login với profile không tồn tại phải thất bại");
                Assert(resCancelNotFound.Error?.Code == "ENTITY_NOT_FOUND", $"Mã lỗi phải là ENTITY_NOT_FOUND, nhận được: {resCancelNotFound.Error?.Code}");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 8: Unregister dọn dẹp các handler sạch sẽ khỏi BridgeDispatcher.
    /// </summary>
    public static async Task Test_Unregister_RemovesHandlersCleanly()
    {
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_bridge_unreg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                using var coordinator = new LoginCoordinator(registry);
                var probe = new SessionProbe(registry);
                var handlers = new BrowserHandlers(registry, coordinator, probe);

                var dispatcher = new BridgeDispatcher();
                handlers.Register(dispatcher);

                // Hủy đăng ký toàn bộ
                handlers.Unregister(dispatcher);

                // Dispatch lệnh đã hủy: do lệnh vẫn nằm trong allow-list nhưng không còn handler -> INTERNAL_ERROR
                var req = CreateRequest("browser.list_profiles", new BrowserListProfilesRequest());
                var res = await dispatcher.DispatchAsync(req, CancellationToken.None);

                Assert(!res.Success, "Lệnh sau khi Unregister không được thành công");
                Assert(res.Error?.Code == "INTERNAL_ERROR", $"Mã lỗi phải là INTERNAL_ERROR khi handler đã hủy, nhận được: {res.Error?.Code}");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Test 9: Đảm bảo ProviderUrlResolver giải quyết chính xác URL cho ChatGPT, Gemini, MuseAI, TikTok
    /// và lệnh browser.request_login tiếp nhận providerKey/targetUrl hợp lệ.
    /// </summary>
    public static async Task Test_ProviderUrlResolution_And_LoginDirectToPage()
    {
        // 1. Kiểm tra Resolver độc lập
        Assert(ProviderUrlResolver.ResolveUrl("chatgpt-web") == "https://chatgpt.com/", "chatgpt-web phải giải quyết ra https://chatgpt.com/");
        Assert(ProviderUrlResolver.ResolveUrl("chatgpt") == "https://chatgpt.com/", "chatgpt phải giải quyết ra https://chatgpt.com/");
        Assert(ProviderUrlResolver.ResolveUrl("gemini-web") == "https://gemini.google.com/", "gemini-web phải giải quyết ra https://gemini.google.com/");
        Assert(ProviderUrlResolver.ResolveUrl("museai") == "https://muse.ai/", "museai phải giải quyết ra https://muse.ai/");
        Assert(ProviderUrlResolver.ResolveUrl("museai-video") == "https://muse.ai/", "museai-video phải giải quyết ra https://muse.ai/");
        Assert(ProviderUrlResolver.ResolveUrl("tiktok") == "https://www.tiktok.com/", "tiktok phải giải quyết ra https://www.tiktok.com/");
        Assert(ProviderUrlResolver.ResolveUrl(null, "MuseAI") == "https://muse.ai/", "Kind MuseAI phải giải quyết ra https://muse.ai/");
        Assert(ProviderUrlResolver.ResolveUrl(null, "SharedAI") == "https://chatgpt.com/", "Kind SharedAI phải giải quyết ra https://chatgpt.com/");
        Assert(ProviderUrlResolver.ResolveUrl(null, null, "https://example.com/custom") == "https://example.com/custom", "Custom URL hợp lệ phải được tôn trọng");

        // 2. Kiểm tra qua Bridge Handler
        var (connection, factory) = await CreateInMemoryDbContextFactoryAsync();
        await using (connection)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "titoai_test_bridge_purl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var registry = new ProfileRegistry(factory, tempDir);
                using var coordinator = new LoginCoordinator(registry);
                var probe = new SessionProbe(registry);
                var handlers = new BrowserHandlers(registry, coordinator, probe);

                var dispatcher = new BridgeDispatcher();
                handlers.Register(dispatcher);

                // Khởi tạo profile có ProviderKey
                await registry.EnsureProfileAsync("prof_chatgpt", BrowserProfileKind.SharedAI);
                await using (var db = factory())
                {
                    var p = await db.BrowserProfiles.FindAsync("prof_chatgpt");
                    if (p != null)
                    {
                        p.ProviderKey = "chatgpt-web";
                        await db.SaveChangesAsync();
                    }
                }

                // Gửi request_login với providerKey
                var req = CreateRequest("browser.request_login", new BrowserRequestLoginRequest(
                    ProfileId: "prof_chatgpt",
                    ProviderKey: "chatgpt-web"
                ));
                var res = await dispatcher.DispatchAsync(req, CancellationToken.None);

                Assert(res.Success, "browser.request_login với providerKey phải thành công");
                var data = DeserializeData<BrowserRequestLoginResponseData>(res);
                Assert(!data.Queued, "Profile đầu tiên phải được cấp slot trực tiếp");
                Assert(data.VisibilityState == BrowserVisibilityState.VisibleActive, "VisibilityState phải là VisibleActive");
                Assert(coordinator.CurrentVisibleProfileId == "prof_chatgpt", "CurrentVisibleProfileId phải là prof_chatgpt");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Chạy toàn bộ các bài kiểm tra khói của Track R1 (Desktop Browser Handlers & Bridge Integration).
    /// </summary>
    public static async Task RunAllAsync()
    {
        Console.WriteLine("\n[Track R1] Desktop Browser Handlers & Bridge Integration Smoke Checks:");

        async Task RunAsync(string name, Func<Task> test)
        {
            Console.Write($"  [RUN] {name} ... ");
            try
            {
                await test();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL");
                Console.ResetColor();
                Console.WriteLine($"        -> Lỗi: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        await RunAsync("Test 1: List global browser profiles", Test_ListProfiles_GlobalScope);
        await RunAsync("Test 2: Request login (Concurrent VisibleActive for independent profiles, no app queue)", Test_RequestLogin_ConcurrentLogins_IndependentVisibleActive);
        await RunAsync("Test 3: Cancel login (Closes specific profile, independent profile remains active)", Test_CancelLogin_ReleasesSpecificProfileSlot_OtherProfileRemainsActive);
        await RunAsync("Test 4: Check session (Real Unverified / Connected status, no fake PASS)", Test_CheckSession_RealStatus_UnverifiedAndConnected_NoFakePass);
        await RunAsync("Test 5: Unknown command rejected with UNKNOWN_COMMAND", Test_UnknownCommand_RejectedWithUnknownCommand);
        await RunAsync("Test 6: Invalid / missing payload rejected with INVALID_ARGUMENT", Test_PayloadValidation_InvalidArguments_Rejected);
        await RunAsync("Test 7: Non-existent profile rejected with ENTITY_NOT_FOUND", Test_EntityNotFound_ForNonExistentProfile_OnLoginAndCancel);
        await RunAsync("Test 8: Unregister handlers cleans up registration", Test_Unregister_RemovesHandlersCleanly);
        await RunAsync("Test 9: Provider URL resolution & login directly to provider page", Test_ProviderUrlResolution_And_LoginDirectToPage);
    }

    private static RequestEnvelope CreateRequest(string command, object? payload)
    {
        JsonElement payloadElement;
        if (payload is null)
        {
            using var doc = JsonDocument.Parse("null");
            payloadElement = doc.RootElement.Clone();
        }
        else if (payload is JsonElement elem)
        {
            payloadElement = elem;
        }
        else
        {
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            using var doc = JsonDocument.Parse(jsonBytes);
            payloadElement = doc.RootElement.Clone();
        }

        return new RequestEnvelope(
            Id: Guid.NewGuid(),
            Type: "request",
            Command: command,
            Payload: payloadElement,
            ClientTimestampUtc: DateTimeOffset.UtcNow,
            ContractVersion: BridgeContract.Version
        );
    }

    private static RequestEnvelope CreateRequestWithRawJson(string command, string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var payloadElement = doc.RootElement.Clone();

        return new RequestEnvelope(
            Id: Guid.NewGuid(),
            Type: "request",
            Command: command,
            Payload: payloadElement,
            ClientTimestampUtc: DateTimeOffset.UtcNow,
            ContractVersion: BridgeContract.Version
        );
    }

    private static T DeserializeData<T>(ResponseEnvelope response)
    {
        Assert(response.Data.HasValue, "Response.Data phải có giá trị");
        var json = response.Data!.Value.GetRawText();
        var obj = JsonSerializer.Deserialize<T>(json, JsonOptions);
        Assert(obj is not null, "Deserialization kết quả trả về null");
        return obj!;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
