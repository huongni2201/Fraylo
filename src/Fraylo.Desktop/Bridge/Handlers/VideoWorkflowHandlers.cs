using System.Text.Json;
using Fraylo.Application.Content;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Content;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers điều phối quy trình sản xuất video từ ảnh (workflow.*) theo PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN.
/// </summary>
public sealed class VideoWorkflowHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ProjectWorkflowService _workflowService;

    public VideoWorkflowHandlers(ProjectWorkflowService workflowService)
    {
        _workflowService = workflowService ?? throw new ArgumentNullException(nameof(workflowService));
    }

    /// <summary>
    /// Đăng ký toàn bộ danh mục lệnh workflow.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("workflow.get_config");
        dispatcher.AllowCommand("workflow.save_config");
        dispatcher.AllowCommand("workflow.start");
        dispatcher.AllowCommand("workflow.list_scenes");
        dispatcher.AllowCommand("workflow.update_scene_input");
        dispatcher.AllowCommand("workflow.pause");
        dispatcher.AllowCommand("workflow.resume");
        dispatcher.AllowCommand("workflow.preview_ai_prompt");
        dispatcher.AllowCommand("workflow.list_content_items");
        dispatcher.AllowCommand("workflow.update_content_item");
        dispatcher.AllowCommand("workflow.create_scene_video");
        dispatcher.AllowCommand("workflow.get_storyboard");

        dispatcher.RegisterHandler("workflow.get_config", HandleGetConfigAsync);
        dispatcher.RegisterHandler("workflow.save_config", HandleSaveConfigAsync);
        dispatcher.RegisterHandler("workflow.start", HandleStartWorkflowAsync);
        dispatcher.RegisterHandler("workflow.list_scenes", HandleListScenesAsync);
        dispatcher.RegisterHandler("workflow.update_scene_input", HandleUpdateSceneInputAsync);
        dispatcher.RegisterHandler("workflow.pause", HandlePauseWorkflowAsync);
        dispatcher.RegisterHandler("workflow.resume", HandleResumeWorkflowAsync);
        dispatcher.RegisterHandler("workflow.preview_ai_prompt", HandlePreviewAiPromptAsync);
        dispatcher.RegisterHandler("workflow.list_content_items", HandleListContentItemsAsync);
        dispatcher.RegisterHandler("workflow.update_content_item", HandleUpdateContentItemAsync);
        dispatcher.RegisterHandler("workflow.create_scene_video", HandleCreateSceneVideoAsync);
        dispatcher.RegisterHandler("workflow.get_storyboard", HandleGetStoryboardAsync);
    }

    public async Task<ResponseEnvelope> HandleGetStoryboardAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        try
        {
            var payload = ParsePayload<GetProjectWorkflowConfigRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Thiếu tham số bắt buộc 'projectId'.", retryable: false);
            var storyboard = await _workflowService.GetStoryboardAsync(payload.ProjectId, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new { storyboard });
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleGetConfigAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<GetProjectWorkflowConfigRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId'.",
                    retryable: false);
            }

            var config = await _workflowService.GetWorkflowConfigAsync(payload.ProjectId, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, config);
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleSaveConfigAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<SaveProjectWorkflowConfigRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId'.",
                    retryable: false);
            }

            var saved = await _workflowService.SaveWorkflowConfigAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, saved);
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleStartWorkflowAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<StartProjectWorkflowRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId'.",
                    retryable: false);
            }

            var result = await _workflowService.StartWorkflowAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleListScenesAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<GetProjectWorkflowConfigRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId'.",
                    retryable: false);
            }

            var scenes = await _workflowService.ListSceneWorkflowDetailsAsync(payload.ProjectId, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new { scenes });
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleUpdateSceneInputAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<UpdateSceneWorkflowInputRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId) || string.IsNullOrWhiteSpace(payload.SceneId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId' hoặc 'sceneId'.",
                    retryable: false);
            }

            var updated = await _workflowService.UpdateSceneWorkflowInputAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, updated);
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandlePauseWorkflowAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<GetProjectWorkflowConfigRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId'.",
                    retryable: false);
            }

            var result = await _workflowService.PauseWorkflowAsync(payload.ProjectId, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleResumeWorkflowAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<GetProjectWorkflowConfigRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId'.",
                    retryable: false);
            }

            var result = await _workflowService.ResumeWorkflowAsync(payload.ProjectId, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandlePreviewAiPromptAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<PreviewAiPromptRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId'.",
                    retryable: false);
            }

            var result = await _workflowService.PreviewAiPromptAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleListContentItemsAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var payload = ParsePayload<GetProjectWorkflowConfigRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId'.",
                    retryable: false);
            }

            var items = await _workflowService.ListVideoContentItemsAsync(payload.ProjectId, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, items);
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleUpdateContentItemAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var payload = ParsePayload<UpdateVideoContentItemRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId) || string.IsNullOrWhiteSpace(payload.Id))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId' hoặc 'id'.",
                    retryable: false);
            }

            var updated = await _workflowService.UpdateVideoContentItemAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, updated);
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleCreateSceneVideoAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var payload = ParsePayload<CreateSceneVideoRequest>(request);
            if (payload == null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Thiếu tham số bắt buộc 'projectId'.",
                    retryable: false);
            }

            int count = await _workflowService.CreateSceneVideoAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new { Enqueued = count > 0, Count = count, Message = "Đã đưa tác vụ tạo video vào hàng đợi." });
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    private static T? ParsePayload<T>(RequestEnvelope request)
    {
        if (request.Payload is JsonElement jsonElement)
        {
            return JsonSerializer.Deserialize<T>(jsonElement.GetRawText(), JsonOptions);
        }

        var serialized = JsonSerializer.Serialize(request.Payload, JsonOptions);
        return JsonSerializer.Deserialize<T>(serialized, JsonOptions);
    }

    private static ResponseEnvelope MapExceptionToError(Guid requestId, Exception ex)
    {
        return ex switch
        {
            ProjectWorkflowValidationException vex => ResponseEnvelopeHelper.CreateError(
                requestId, "VALIDATION_FAILED", vex.Message, retryable: false),

            JobValidationException vex => ResponseEnvelopeHelper.CreateError(
                requestId, vex.ErrorCode, vex.Message, retryable: false),

            ProviderNotConfiguredException pex => ResponseEnvelopeHelper.CreateError(
                requestId, pex.ErrorCode, pex.Message, retryable: false),

            StorageRootUnavailableException sex => ResponseEnvelopeHelper.CreateError(
                requestId, sex.ErrorCode, sex.Message, retryable: false),

            UnauthorizedAccessException uex => ResponseEnvelopeHelper.CreateError(
                requestId, "INVALID_ARGUMENT", uex.Message, retryable: false),

            SceneConcurrencyConflictException cex => ResponseEnvelopeHelper.CreateError(
                requestId, "CONCURRENCY_CONFLICT", cex.Message, retryable: false),

            _ => ResponseEnvelopeHelper.CreateError(
                requestId, "INTERNAL_ERROR", "Đã xảy ra lỗi nội bộ khi xử lý quy trình sản xuất video.", retryable: false)
        };
    }
}
