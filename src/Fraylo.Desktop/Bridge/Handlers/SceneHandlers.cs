using System.Text.Json;
using Fraylo.Application.Projects;
using Fraylo.Application.Scenes;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Scenes;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối các lệnh quản lý cảnh (scene.*)
/// theo contracts.md v1.0.4 §1.5 B và G2 Core Business.
/// </summary>
public sealed class SceneHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SceneService _sceneService;

    public SceneHandlers(SceneService sceneService)
    {
        _sceneService = sceneService ?? throw new ArgumentNullException(nameof(sceneService));
    }

    /// <summary>
    /// Đăng ký toàn bộ danh mục lệnh scene.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("scene.list_by_project");
        dispatcher.AllowCommand("scene.get");
        dispatcher.AllowCommand("scene.create");
        dispatcher.AllowCommand("scene.update");
        dispatcher.AllowCommand("scene.delete");
        dispatcher.AllowCommand("scene.reorder");

        dispatcher.RegisterHandler("scene.list_by_project", HandleListByProjectAsync);
        dispatcher.RegisterHandler("scene.get", HandleGetAsync);
        dispatcher.RegisterHandler("scene.create", HandleCreateAsync);
        dispatcher.RegisterHandler("scene.update", HandleUpdateAsync);
        dispatcher.RegisterHandler("scene.delete", HandleDeleteAsync);
        dispatcher.RegisterHandler("scene.reorder", HandleReorderAsync);
    }

    /// <summary>
    /// Hủy đăng ký toàn bộ danh mục lệnh scene.* khỏi BridgeDispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("scene.list_by_project");
        dispatcher.UnregisterHandler("scene.get");
        dispatcher.UnregisterHandler("scene.create");
        dispatcher.UnregisterHandler("scene.update");
        dispatcher.UnregisterHandler("scene.delete");
        dispatcher.UnregisterHandler("scene.reorder");
    }

    public async Task<ResponseEnvelope> HandleListByProjectAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<SceneListByProjectRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu các trường bắt buộc 'projectId'.",
                    retryable: false);
            }

            var scenes = await _sceneService.ListByProjectAsync(payload.ProjectId, cancellationToken);
            var data = new SceneListByProjectResponseData(scenes);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, data);
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

    public async Task<ResponseEnvelope> HandleGetAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<SceneGetRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.ProjectId) ||
                string.IsNullOrWhiteSpace(payload.SceneId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu các trường bắt buộc (projectId, sceneId).",
                    retryable: false);
            }

            var scene = await _sceneService.GetAsync(payload.ProjectId, payload.SceneId, cancellationToken);
            var data = new SceneGetResponseData(scene);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, data);
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

    public async Task<ResponseEnvelope> HandleCreateAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<SceneCreateRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu các trường bắt buộc 'projectId'.",
                    retryable: false);
            }

            var result = await _sceneService.CreateAsync(payload, cancellationToken);
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

    public async Task<ResponseEnvelope> HandleUpdateAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<SceneUpdateRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.ProjectId) ||
                string.IsNullOrWhiteSpace(payload.SceneId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu các trường bắt buộc (projectId, sceneId).",
                    retryable: false);
            }

            var scene = await _sceneService.UpdateAsync(payload, cancellationToken);
            var data = new SceneUpdateResponseData(scene);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, data);
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

    public async Task<ResponseEnvelope> HandleDeleteAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<SceneDeleteRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.ProjectId) ||
                string.IsNullOrWhiteSpace(payload.SceneId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu các trường bắt buộc (projectId, sceneId).",
                    retryable: false);
            }

            var result = await _sceneService.DeleteAsync(payload.ProjectId, payload.SceneId, cancellationToken);
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

    public async Task<ResponseEnvelope> HandleReorderAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<SceneReorderRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.ProjectId) ||
                payload.SceneIds is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu các trường bắt buộc (projectId, sceneIds).",
                    retryable: false);
            }

            var result = await _sceneService.ReorderAsync(payload, cancellationToken);
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
            SceneNotFoundException notFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", notFound.Message, retryable: false),

            ProjectNotFoundException prjNotFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", prjNotFound.Message, retryable: false),

            SceneValidationException validation =>
                ResponseEnvelopeHelper.CreateError(requestId, "VALIDATION_FAILED", validation.Message, retryable: false),

            ProjectValidationException prjValidation =>
                ResponseEnvelopeHelper.CreateError(requestId, "VALIDATION_FAILED", prjValidation.Message, retryable: false),

            SceneConcurrencyConflictException sceneConflict =>
                ResponseEnvelopeHelper.CreateError(requestId, "CONCURRENCY_CONFLICT", sceneConflict.Message, retryable: false),

            ProjectConcurrencyConflictException prjConflict =>
                ResponseEnvelopeHelper.CreateError(requestId, "CONCURRENCY_CONFLICT", prjConflict.Message, retryable: false),

            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xử lý nội bộ: {ex.Message}", retryable: false)
        };
    }
}
