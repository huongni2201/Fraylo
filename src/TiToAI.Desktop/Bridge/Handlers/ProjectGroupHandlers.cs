using System.Text.Json;
using TiToAI.Application.Projects;
using TiToAI.Contracts.Bridge;
using TiToAI.Contracts.Projects;

namespace TiToAI.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận các lệnh nhóm dự án (projectgroup.*).
/// </summary>
public sealed class ProjectGroupHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ProjectGroupService _projectGroupService;

    public ProjectGroupHandlers(ProjectGroupService projectGroupService)
    {
        _projectGroupService = projectGroupService ?? throw new ArgumentNullException(nameof(projectGroupService));
    }

    /// <summary>
    /// Đăng ký danh mục lệnh projectgroup.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("projectgroup.list");
        dispatcher.AllowCommand("projectgroup.save");
        dispatcher.AllowCommand("projectgroup.delete");

        dispatcher.RegisterHandler("projectgroup.list", HandleListAsync);
        dispatcher.RegisterHandler("projectgroup.save", HandleSaveAsync);
        dispatcher.RegisterHandler("projectgroup.delete", HandleDeleteAsync);
    }

    /// <summary>
    /// Hủy đăng ký danh mục lệnh projectgroup.* khỏi BridgeDispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("projectgroup.list");
        dispatcher.UnregisterHandler("projectgroup.save");
        dispatcher.UnregisterHandler("projectgroup.delete");
    }

    private async Task<ResponseEnvelope> HandleListAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var groups = await _projectGroupService.ListAllAsync(cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new ProjectGroupListResponseData(groups));
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

    private async Task<ResponseEnvelope> HandleSaveAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ProjectGroupSaveRequest>(request);
            if (payload is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu dữ liệu nhóm dự án.",
                    retryable: false);
            }

            var group = await _projectGroupService.SaveAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new ProjectGroupSaveResponseData(group));
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

    private async Task<ResponseEnvelope> HandleDeleteAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ProjectGroupDeleteRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.GroupId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường bắt buộc 'groupId'.",
                    retryable: false);
            }

            var result = await _projectGroupService.DeleteAsync(payload.GroupId, cancellationToken);
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
            ProjectGroupNotFoundException notFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", notFound.Message, retryable: false),

            ProjectValidationException validation =>
                ResponseEnvelopeHelper.CreateError(requestId, "VALIDATION_FAILED", validation.Message, retryable: false),

            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xử lý nội bộ: {ex.Message}", retryable: false)
        };
    }
}
