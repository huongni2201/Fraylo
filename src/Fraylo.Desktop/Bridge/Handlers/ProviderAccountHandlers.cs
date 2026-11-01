using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Accounts;
using Fraylo.Contracts.Accounts;
using Fraylo.Contracts.Bridge;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối các lệnh quản lý tài khoản Provider toàn hệ thống (provider_account.*)
/// theo execution-contract-v1.1 và plan 1.2 §3.2.
/// </summary>
public sealed class ProviderAccountHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ProviderAccountService _providerAccountService;

    public ProviderAccountHandlers(ProviderAccountService providerAccountService)
    {
        _providerAccountService = providerAccountService ?? throw new ArgumentNullException(nameof(providerAccountService));
    }

    /// <summary>
    /// Đăng ký toàn bộ danh mục lệnh provider_account.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("provider_account.list");
        dispatcher.AllowCommand("provider_account.create");
        dispatcher.AllowCommand("provider_account.update");
        dispatcher.AllowCommand("provider_account.probe_session");
        dispatcher.AllowCommand("provider_account.enable");
        dispatcher.AllowCommand("provider_account.delete");

        dispatcher.RegisterHandler("provider_account.list", HandleListAsync);
        dispatcher.RegisterHandler("provider_account.create", HandleCreateAsync);
        dispatcher.RegisterHandler("provider_account.update", HandleUpdateAsync);
        dispatcher.RegisterHandler("provider_account.probe_session", HandleProbeSessionAsync);
        dispatcher.RegisterHandler("provider_account.enable", HandleEnableAsync);
        dispatcher.RegisterHandler("provider_account.delete", HandleDeleteAsync);
    }

    /// <summary>
    /// Hủy đăng ký danh mục lệnh provider_account.* khỏi BridgeDispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("provider_account.list");
        dispatcher.UnregisterHandler("provider_account.create");
        dispatcher.UnregisterHandler("provider_account.update");
        dispatcher.UnregisterHandler("provider_account.probe_session");
        dispatcher.UnregisterHandler("provider_account.enable");
        dispatcher.UnregisterHandler("provider_account.delete");
    }

    public async Task<ResponseEnvelope> HandleListAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ProviderAccountListRequest>(request);
            var accounts = await _providerAccountService.ListAccountsAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new ProviderAccountListResponseData(accounts));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "INTERNAL_ERROR",
                $"Lỗi khi liệt kê tài khoản: {ex.Message}",
                retryable: true);
        }
    }

    public async Task<ResponseEnvelope> HandleCreateAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<CreateProviderAccountRequest>(request);
            if (payload is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload tạo tài khoản không hợp lệ hoặc thiếu dữ liệu.",
                    retryable: false);
            }

            var created = await _providerAccountService.CreateAccountAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new CreateProviderAccountResponseData(created));
        }
        catch (ProviderAccountValidationException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message, retryable: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "INTERNAL_ERROR",
                $"Lỗi tạo tài khoản: {ex.Message}",
                retryable: false);
        }
    }

    public async Task<ResponseEnvelope> HandleUpdateAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<UpdateProviderAccountRequest>(request);
            if (payload is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload cập nhật tài khoản không hợp lệ.",
                    retryable: false);
            }

            var updated = await _providerAccountService.UpdateAccountAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new UpdateProviderAccountResponseData(updated));
        }
        catch (ProviderAccountConcurrencyException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "CONCURRENCY_CONFLICT", ex.Message, retryable: true);
        }
        catch (ProviderAccountValidationException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message, retryable: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "INTERNAL_ERROR",
                $"Lỗi cập nhật tài khoản: {ex.Message}",
                retryable: false);
        }
    }

    public async Task<ResponseEnvelope> HandleProbeSessionAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ProbeProviderAccountSessionRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu trường 'id' tài khoản cần kiểm tra phiên.",
                    retryable: false);
            }

            var result = await _providerAccountService.ProbeAccountSessionAsync(payload.Id, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (ProviderAccountValidationException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message, retryable: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "INTERNAL_ERROR",
                $"Lỗi kiểm tra phiên tài khoản: {ex.Message}",
                retryable: true);
        }
    }

    public async Task<ResponseEnvelope> HandleEnableAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<EnableProviderAccountRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số 'id' tài khoản.",
                    retryable: false);
            }

            var result = await _providerAccountService.EnableAccountAsync(payload.Id, payload.Enabled, payload.ExpectedRevision, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new EnableProviderAccountResponseData(result));
        }
        catch (ProviderAccountConcurrencyException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "CONCURRENCY_CONFLICT", ex.Message, retryable: true);
        }
        catch (ProviderAccountValidationException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message, retryable: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "INTERNAL_ERROR",
                $"Lỗi bật/tắt tài khoản: {ex.Message}",
                retryable: false);
        }
    }

    public async Task<ResponseEnvelope> HandleDeleteAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<DeleteProviderAccountRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số 'id' tài khoản cần xoá.",
                    retryable: false);
            }

            var result = await _providerAccountService.DeleteAccountAsync(payload.Id, payload.ExpectedRevision, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new DeleteProviderAccountResponseData(result));
        }
        catch (ProviderAccountConcurrencyException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "CONCURRENCY_CONFLICT", ex.Message, retryable: true);
        }
        catch (ProviderAccountValidationException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message, retryable: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "INTERNAL_ERROR",
                $"Lỗi xoá tài khoản: {ex.Message}",
                retryable: false);
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
}
