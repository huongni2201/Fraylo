using System.Text.Json;
using Fraylo.Application.Documents;
using Fraylo.Application.Scenes;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Documents;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối các lệnh xuất tài liệu văn bản (document.*)
/// theo contracts.md v1.0.4 và implementation/T09c-document-export.md.
/// </summary>
public sealed class DocumentHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DocumentExportService _documentExportService;

    public DocumentHandlers(DocumentExportService documentExportService)
    {
        _documentExportService = documentExportService ?? throw new ArgumentNullException(nameof(documentExportService));
    }

    /// <summary>
    /// Đăng ký toàn bộ danh mục lệnh document.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("document.export");
        dispatcher.RegisterHandler("document.export", HandleExportAsync);
    }

    /// <summary>
    /// Hủy đăng ký danh mục lệnh document.* khỏi BridgeDispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("document.export");
    }

    public async Task<ResponseEnvelope> HandleExportAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<DocumentExportRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.SceneId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu các trường bắt buộc 'sceneId'.",
                    retryable: false);
            }

            var result = await _documentExportService.ExportDocumentAsync(payload, cancellationToken);
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
            DocumentExportException docEx =>
                ResponseEnvelopeHelper.CreateError(requestId, "VALIDATION_FAILED", docEx.Message, retryable: false),

            SceneNotFoundException sceneNotFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", sceneNotFound.Message, retryable: false),

            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xuất tài liệu: {ex.Message}", retryable: false)
        };
    }
}
