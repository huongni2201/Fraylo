using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Workflows;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Workflows;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối các lệnh định nghĩa workflow có version (workflow.*)
/// theo kiến trúc T18d và execution-contract-v1.1 §6.
/// </summary>
public sealed class WorkflowHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WorkflowDefinitionService _workflowService;

    public WorkflowHandlers(WorkflowDefinitionService workflowService)
    {
        _workflowService = workflowService ?? throw new ArgumentNullException(nameof(workflowService));
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
    /// Đăng ký danh mục lệnh workflow.* vào Bridge Dispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("workflow.get_active");
        dispatcher.AllowCommand("workflow.save_version");
        dispatcher.AllowCommand("workflow.activate");

        dispatcher.RegisterHandler("workflow.get_active", HandleGetActiveAsync);
        dispatcher.RegisterHandler("workflow.save_version", HandleSaveVersionAsync);
        dispatcher.RegisterHandler("workflow.activate", HandleActivateAsync);
    }

    /// <summary>
    /// Hủy đăng ký danh mục lệnh workflow.* khỏi Bridge Dispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("workflow.get_active");
        dispatcher.UnregisterHandler("workflow.save_version");
        dispatcher.UnregisterHandler("workflow.activate");
    }

    public async Task<ResponseEnvelope> HandleGetActiveAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var response = await _workflowService.GetActiveWorkflowAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<ResponseEnvelope> HandleSaveVersionAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<WorkflowSaveVersionRequest>(request);
            if (payload is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload rỗng hoặc không đúng định dạng.",
                    retryable: false);
            }

            var response = await _workflowService.SaveVersionAsync(payload, cancellationToken).ConfigureAwait(false);
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

    public async Task<ResponseEnvelope> HandleActivateAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<WorkflowActivateRequest>(request);
            if (payload is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload rỗng hoặc không đúng định dạng.",
                    retryable: false);
            }

            var response = await _workflowService.ActivateVersionAsync(payload, cancellationToken).ConfigureAwait(false);
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

    private static ResponseEnvelope MapExceptionToError(Guid requestId, Exception ex)
    {
        return ex switch
        {
            WorkflowValidationException val =>
                ResponseEnvelopeHelper.CreateError(requestId, "VALIDATION_FAILED", val.Message, retryable: false),

            WorkflowConcurrencyException conc =>
                ResponseEnvelopeHelper.CreateError(requestId, "CONCURRENCY_CONFLICT", conc.Message, retryable: true),

            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            KeyNotFoundException notFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", notFound.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xử lý workflow: {ex.Message}", retryable: false)
        };
    }
}
