using System.Text.Json;
using Fraylo.Application.Projects;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Projects;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối các lệnh quản lý dự án (project.*)
/// theo contracts.md v1.0.4 §1.5 B và G2 Core Business.
/// </summary>
public sealed class ProjectHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ProjectService _projectService;

    public ProjectHandlers(ProjectService projectService)
    {
        _projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
    }

    /// <summary>
    /// Đăng ký toàn bộ danh mục lệnh project.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("project.list");
        dispatcher.AllowCommand("project.get");
        dispatcher.AllowCommand("project.create");
        dispatcher.AllowCommand("project.update");
        dispatcher.AllowCommand("project.delete");

        dispatcher.RegisterHandler("project.list", HandleListAsync);
        dispatcher.RegisterHandler("project.get", HandleGetAsync);
        dispatcher.RegisterHandler("project.create", HandleCreateAsync);
        dispatcher.RegisterHandler("project.update", HandleUpdateAsync);
        dispatcher.RegisterHandler("project.delete", HandleDeleteAsync);
    }

    /// <summary>
    /// Hủy đăng ký toàn bộ danh mục lệnh project.* khỏi BridgeDispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("project.list");
        dispatcher.UnregisterHandler("project.get");
        dispatcher.UnregisterHandler("project.create");
        dispatcher.UnregisterHandler("project.update");
        dispatcher.UnregisterHandler("project.delete");
    }

    public async Task<ResponseEnvelope> HandleListAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var projects = await _projectService.ListAllAsync(cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(
                request.Id,
                new ProjectListResponseData(projects));
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
            var payload = ParsePayload<ProjectGetRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường bắt buộc 'projectId'.",
                    retryable: false);
            }

            var project = await _projectService.GetAsync(payload.ProjectId, cancellationToken);
            var data = new ProjectGetResponseData(project);
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
            var payload = ParsePayload<ProjectCreateRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Name))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường bắt buộc 'name'.",
                    retryable: false);
            }

            var result = await _projectService.CreateAsync(payload, cancellationToken);
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
            var payload = ParsePayload<ProjectUpdateRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường bắt buộc 'projectId'.",
                    retryable: false);
            }

            var project = await _projectService.UpdateAsync(payload, cancellationToken);
            var data = new ProjectUpdateResponseData(project);
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
            var payload = ParsePayload<ProjectDeleteRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProjectId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường bắt buộc 'projectId'.",
                    retryable: false);
            }

            var result = await _projectService.DeleteAsync(payload.ProjectId, cancellationToken);
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
            ProjectNotFoundException notFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", notFound.Message, retryable: false),

            ProjectValidationException validation =>
                ResponseEnvelopeHelper.CreateError(requestId, "VALIDATION_FAILED", validation.Message, retryable: false),

            ProjectConcurrencyConflictException prjConflict =>
                ResponseEnvelopeHelper.CreateError(requestId, "CONCURRENCY_CONFLICT", prjConflict.Message, retryable: false),

            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xử lý nội bộ: {ex.Message}", retryable: false)
        };
    }
}
