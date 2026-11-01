using System.Text.Json;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Settings;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối các lệnh cài đặt hệ thống (settings.*)
/// theo contracts.md v1.0.4 và SETTINGS_PLAN §1–5.
/// </summary>
public sealed class SettingsHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settingsService;

    public SettingsHandlers(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <summary>
    /// Đăng ký toàn bộ danh mục lệnh settings.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("settings.get");
        dispatcher.AllowCommand("settings.save");
        dispatcher.AllowCommand("settings.browse_folder");
        dispatcher.AllowCommand("settings.validate_roots");

        dispatcher.RegisterHandler("settings.get", HandleGetAsync);
        dispatcher.RegisterHandler("settings.save", HandleSaveAsync);
        dispatcher.RegisterHandler("settings.browse_folder", HandleBrowseFolderAsync);
        dispatcher.RegisterHandler("settings.validate_roots", HandleValidateRootsAsync);
    }

    /// <summary>
    /// Hủy đăng ký danh mục lệnh settings.* khỏi BridgeDispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("settings.get");
        dispatcher.UnregisterHandler("settings.save");
        dispatcher.UnregisterHandler("settings.browse_folder");
        dispatcher.UnregisterHandler("settings.validate_roots");
    }

    public async Task<ResponseEnvelope> HandleGetAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var settings = await _settingsService.GetSettingsAsync(cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, settings);
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

    public async Task<ResponseEnvelope> HandleSaveAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<SettingsSaveRequest>(request);
            if (payload is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload cài đặt rỗng hoặc không đúng định dạng.",
                    retryable: false);
            }

            var updatedSettings = await _settingsService.SaveSettingsAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, updatedSettings);
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

    public async Task<ResponseEnvelope> HandleBrowseFolderAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<BrowseFolderRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Target))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường 'target' ('documents' | 'images' | 'videos').",
                    retryable: false);
            }

            var result = await _settingsService.BrowseFolderAsync(payload, cancellationToken);
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

    public async Task<ResponseEnvelope> HandleValidateRootsAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ValidateRootsRequest>(request);
            if (payload is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload xác thực rỗng hoặc không đúng định dạng.",
                    retryable: false);
            }

            var result = await _settingsService.ValidateRootsAsync(payload, cancellationToken);
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

    private static T? ParsePayload<T>(RequestEnvelope request) where T : class
    {
        var element = request.Payload;
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
    }

    private static ResponseEnvelope MapExceptionToError(Guid requestId, Exception ex)
    {
        return ex switch
        {
            SettingsConcurrencyException concurrency =>
                ResponseEnvelopeHelper.CreateError(
                    requestId,
                    "CONCURRENCY_CONFLICT",
                    concurrency.Message,
                    retryable: true,
                    details: JsonSerializer.SerializeToElement(new { expectedRevision = concurrency.ExpectedRevision, currentRevision = concurrency.CurrentRevision })),

            SettingsValidationException validation =>
                ResponseEnvelopeHelper.CreateError(
                    requestId,
                    "VALIDATION_FAILED",
                    validation.Message,
                    retryable: false,
                    details: JsonSerializer.SerializeToElement(new { fieldErrors = validation.FieldErrors })),

            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            KeyNotFoundException notFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", notFound.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xử lý cài đặt: {ex.Message}", retryable: false)
        };
    }
}
