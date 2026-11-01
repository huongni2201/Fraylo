using System.Text.Json;
using Fraylo.Browser;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Browser;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối 4 lệnh quản lý trình duyệt (browser.*)
/// qua WebView2 Bridge theo contracts.md v1.0.4 §1.5 B và G2 Browser Architecture.
/// </summary>
public sealed class BrowserHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IProfileRegistry _profileRegistry;
    private readonly ILoginCoordinator _loginCoordinator;
    private readonly ISessionProbe _sessionProbe;

    public BrowserHandlers(
        IProfileRegistry profileRegistry,
        ILoginCoordinator loginCoordinator,
        ISessionProbe sessionProbe)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
        _loginCoordinator = loginCoordinator ?? throw new ArgumentNullException(nameof(loginCoordinator));
        _sessionProbe = sessionProbe ?? throw new ArgumentNullException(nameof(sessionProbe));
    }

    /// <summary>
    /// Đăng ký toàn bộ 4 lệnh browser.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("browser.list_profiles");
        dispatcher.AllowCommand("browser.request_login");
        dispatcher.AllowCommand("browser.cancel_login");
        dispatcher.AllowCommand("browser.check_session");

        dispatcher.RegisterHandler("browser.list_profiles", HandleListProfilesAsync);
        dispatcher.RegisterHandler("browser.request_login", HandleRequestLoginAsync);
        dispatcher.RegisterHandler("browser.cancel_login", HandleCancelLoginAsync);
        dispatcher.RegisterHandler("browser.check_session", HandleCheckSessionAsync);
    }

    /// <summary>
    /// Hủy đăng ký toàn bộ 4 lệnh browser.* khỏi BridgeDispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("browser.list_profiles");
        dispatcher.UnregisterHandler("browser.request_login");
        dispatcher.UnregisterHandler("browser.cancel_login");
        dispatcher.UnregisterHandler("browser.check_session");
    }

    /// <summary>
    /// Xử lý lệnh browser.list_profiles: Liệt kê toàn bộ profile cấp ứng dụng.
    /// </summary>
    public async Task<ResponseEnvelope> HandleListProfilesAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (request.Payload.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload của browser.list_profiles phải là object.",
                    retryable: false);
            }

            if (request.Payload.ValueKind == JsonValueKind.Object)
            {
                _ = ParsePayload<BrowserListProfilesRequest>(request);
            }

            var profiles = await _profileRegistry.ListProfilesAsync(cancellationToken);

            var dtoList = profiles.Select(MapToDto).ToList();
            var responseData = new BrowserListProfilesResponseData(dtoList);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, responseData);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý lệnh browser.request_login: Yêu cầu cấp slot hiển thị hoặc xếp vào hàng đợi chờ đăng nhập.
    /// </summary>
    public async Task<ResponseEnvelope> HandleRequestLoginAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<BrowserRequestLoginRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProfileId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload yêu cầu đăng nhập thiếu trường 'profileId' bắt buộc.",
                    retryable: false);
            }

            var profile = await _profileRegistry.GetProfileAsync(payload.ProfileId, cancellationToken);
            if (profile is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "ENTITY_NOT_FOUND",
                    $"Hồ sơ trình duyệt '{payload.ProfileId}' không tồn tại.",
                    retryable: false);
            }

            string? providerKey = !string.IsNullOrWhiteSpace(payload.ProviderKey)
                ? payload.ProviderKey
                : profile.ProviderKey;

            string? targetUrl = ProviderUrlResolver.ResolveUrl(providerKey, profile.Kind, payload.TargetUrl);

            var result = await _loginCoordinator.RequestLoginAsync(payload.ProfileId, targetUrl, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý lệnh browser.cancel_login: Hủy phiên hoặc yêu cầu chờ đăng nhập của profile.
    /// </summary>
    public async Task<ResponseEnvelope> HandleCancelLoginAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<BrowserCancelLoginRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProfileId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload yêu cầu hủy đăng nhập thiếu trường 'profileId' bắt buộc.",
                    retryable: false);
            }

            var profile = await _profileRegistry.GetProfileAsync(payload.ProfileId, cancellationToken);
            if (profile is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "ENTITY_NOT_FOUND",
                    $"Hồ sơ trình duyệt '{payload.ProfileId}' không tồn tại.",
                    retryable: false);
            }

            var result = await _loginCoordinator.CancelLoginAsync(payload.ProfileId, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý lệnh browser.check_session: Kiểm tra trạng thái session thực của profile (Unverified / Connected).
    /// </summary>
    public async Task<ResponseEnvelope> HandleCheckSessionAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<BrowserCheckSessionRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProfileId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload kiểm tra phiên thiếu trường 'profileId' bắt buộc.",
                    retryable: false);
            }

            var result = await _sessionProbe.CheckSessionAsync(payload.ProfileId, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    private static BrowserProfileDto MapToDto(BrowserProfileInfo info) => info.ToDto();

    private static T? ParsePayload<T>(RequestEnvelope request) where T : class
    {
        var element = request.Payload;
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Payload JSON không hợp lệ: {ex.Message}", nameof(request), ex);
        }
    }

    private static ResponseEnvelope MapExceptionToError(Guid requestId, Exception ex)
    {
        return ex switch
        {
            KeyNotFoundException notFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", notFound.Message, retryable: false),

            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            InvalidOperationException op =>
                ResponseEnvelopeHelper.CreateError(requestId, "CONCURRENCY_CONFLICT", op.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xử lý nội bộ: {ex.Message}", retryable: false)
        };
    }
}
