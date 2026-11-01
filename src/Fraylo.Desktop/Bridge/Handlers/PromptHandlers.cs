using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Prompts;
using Fraylo.Application.Prompts.Exceptions;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Prompts;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge handlers cho các lệnh prompt template toàn ứng dụng.
/// </summary>
public sealed class PromptHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DbContextOptions<FrayloDbContext>? _dbOptions;
    private readonly Func<FrayloDbContext>? _dbContextFactory;

    public PromptHandlers(DbContextOptions<FrayloDbContext> dbOptions)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
    }

    public PromptHandlers(Func<FrayloDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    private FrayloDbContext CreateDbContext()
    {
        if (_dbContextFactory != null)
        {
            return _dbContextFactory();
        }

        if (_dbOptions != null)
        {
            return new FrayloDbContext(_dbOptions);
        }

        throw new InvalidOperationException("Không có cấu hình khởi tạo FrayloDbContext.");
    }

    private static bool IsEmptyPayload(JsonElement element)
    {
        return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    }

    /// <summary>
    /// Đăng ký các lệnh quản lý prompt hiện hành vào Bridge Dispatcher.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("prompt.list_templates");
        dispatcher.RegisterHandler("prompt.list_templates", HandleListTemplatesAsync);

        dispatcher.AllowCommand("prompt.get_template");
        dispatcher.RegisterHandler("prompt.get_template", HandleGetTemplateAsync);

        dispatcher.AllowCommand("prompt.save_template");
        dispatcher.RegisterHandler("prompt.save_template", HandleSaveTemplateAsync);

        dispatcher.AllowCommand("prompt.activate_template");
        dispatcher.RegisterHandler("prompt.activate_template", HandleActivateTemplateAsync);

        dispatcher.AllowCommand("prompt.preview");
        dispatcher.RegisterHandler("prompt.preview", HandlePreviewAsync);

    }

    /// <summary>
    /// Hủy đăng ký các lệnh prompt khỏi Bridge Dispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("prompt.list_templates");
        dispatcher.UnregisterHandler("prompt.get_template");
        dispatcher.UnregisterHandler("prompt.save_template");
        dispatcher.UnregisterHandler("prompt.activate_template");
        dispatcher.UnregisterHandler("prompt.preview");
    }

    public async Task<ResponseEnvelope> HandleListTemplatesAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await using var db = CreateDbContext();
            var service = new PromptTemplateService(db);
            var result = await service.ListTemplatesAsync(cancellationToken);

            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PromptValidationException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message);
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "INTERNAL_ERROR", $"Lỗi hệ thống khi lấy danh sách template: {ex.Message}");
        }
    }

    public async Task<ResponseEnvelope> HandleGetTemplateAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (IsEmptyPayload(request.Payload))
            {
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Payload không được để trống cho lệnh prompt.get_template.");
            }

            var payload = request.Payload.Deserialize<PromptGetTemplateRequest>(JsonOptions);
            if (payload == null)
            {
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Dữ liệu yêu cầu prompt.get_template không hợp lệ.");
            }

            await using var db = CreateDbContext();
            var service = new PromptTemplateService(db);
            var result = await service.GetTemplateAsync(payload.TemplateId, payload.Purpose, cancellationToken);

            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PromptTemplateNotFoundException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "ENTITY_NOT_FOUND", ex.Message);
        }
        catch (PromptValidationException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message);
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "INTERNAL_ERROR", $"Lỗi hệ thống khi lấy template: {ex.Message}");
        }
    }

    public async Task<ResponseEnvelope> HandleSaveTemplateAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (IsEmptyPayload(request.Payload))
            {
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Payload không được để trống cho lệnh prompt.save_template.");
            }

            var payload = request.Payload.Deserialize<PromptSaveTemplateRequest>(JsonOptions);
            if (payload == null)
            {
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Dữ liệu yêu cầu prompt.save_template không hợp lệ.");
            }

            await using var db = CreateDbContext();
            var service = new PromptTemplateService(db);
            var result = await service.SaveTemplateAsync(payload, cancellationToken);

            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PromptValidationException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message);
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "INTERNAL_ERROR", $"Lỗi hệ thống khi lưu template: {ex.Message}");
        }
    }

    public async Task<ResponseEnvelope> HandleActivateTemplateAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        try
        {
            if (IsEmptyPayload(request.Payload))
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Cần chọn mẫu prompt.");
            var payload = request.Payload.Deserialize<PromptActivateTemplateRequest>(JsonOptions);
            if (payload == null)
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Dữ liệu chọn mẫu không hợp lệ.");
            await using var db = CreateDbContext();
            var result = await new PromptTemplateService(db).ActivateTemplateAsync(payload.TemplateId, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (PromptTemplateNotFoundException ex) { return ResponseEnvelopeHelper.CreateError(request.Id, "ENTITY_NOT_FOUND", ex.Message); }
        catch (PromptValidationException ex) { return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message); }
        catch (Exception ex) { return ResponseEnvelopeHelper.CreateError(request.Id, "INTERNAL_ERROR", $"Không thể chọn mẫu prompt: {ex.Message}"); }
    }

    public async Task<ResponseEnvelope> HandlePreviewAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (IsEmptyPayload(request.Payload))
            {
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Payload không được để trống cho lệnh prompt.preview.");
            }

            var payload = request.Payload.Deserialize<PromptPreviewRequest>(JsonOptions);
            if (payload == null)
            {
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Dữ liệu yêu cầu prompt.preview không hợp lệ.");
            }

            await using var db = CreateDbContext();
            var service = new PromptTemplateService(db);
            var result = await service.PreviewAsync(payload, cancellationToken);

            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PromptTemplateNotFoundException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "ENTITY_NOT_FOUND", ex.Message);
        }
        catch (PromptValidationException ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "VALIDATION_FAILED", ex.Message);
        }
        catch (Exception ex)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "INTERNAL_ERROR", $"Lỗi hệ thống khi render preview: {ex.Message}");
        }
    }

}
