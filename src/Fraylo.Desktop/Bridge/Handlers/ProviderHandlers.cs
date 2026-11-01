using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Accounts;
using Fraylo.Application.Providers;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Providers;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối các lệnh tra cứu Provider AI và năng lực (provider.*)
/// theo kiến trúc T18a và execution-contract-v1.1 §1.2, §3.4.
/// </summary>
public sealed class ProviderHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ProviderRegistry _providerRegistry;
    private readonly ProviderAccountService _accountService;

    public ProviderHandlers(
        ProviderRegistry providerRegistry,
        ProviderAccountService accountService)
    {
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
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

    /// <summary>
    /// Đăng ký danh mục lệnh provider.* vào Bridge Dispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("provider.list");
        dispatcher.AllowCommand("provider.get_capabilities");

        dispatcher.RegisterHandler("provider.list", HandleListAsync);
        dispatcher.RegisterHandler("provider.get_capabilities", HandleGetCapabilitiesAsync);
    }

    /// <summary>
    /// Hủy đăng ký danh mục lệnh provider.* khỏi Bridge Dispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("provider.list");
        dispatcher.UnregisterHandler("provider.get_capabilities");
    }

    public async Task<ResponseEnvelope> HandleListAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var response = await _providerRegistry.GetProviderListAsync(_accountService, cancellationToken).ConfigureAwait(false);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, response);
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

    public Task<ResponseEnvelope> HandleGetCapabilitiesAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ProviderGetCapabilitiesRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProviderKey))
            {
                return Task.FromResult(ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường 'providerKey'.",
                    retryable: false));
            }

            var capabilities = _providerRegistry.GetCapabilities(payload.ProviderKey);
            var response = new ProviderGetCapabilitiesResponseData(payload.ProviderKey, capabilities);
            return Task.FromResult(ResponseEnvelopeHelper.CreateSuccess(request.Id, response));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(MapExceptionToError(request.Id, ex));
        }
    }

    private static ResponseEnvelope MapExceptionToError(Guid requestId, Exception ex)
    {
        return ex switch
        {
            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            KeyNotFoundException notFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", notFound.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xử lý Provider: {ex.Message}", retryable: false)
        };
    }
}
