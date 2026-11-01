using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Prompts.Exceptions;
using Fraylo.Application.Prompts.Models;
using Fraylo.Contracts.Prompts;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Prompts;

/// <summary>
/// Dịch vụ quản lý prompt template toàn ứng dụng và snapshot render bất biến.
/// </summary>
public sealed class PromptTemplateService
{
    private static readonly HashSet<string> ValidPurposes = new(StringComparer.Ordinal)
    {
        "Script",
        "Image",
        "ComposeImage",
        "VideoPrompt",
        "Storyboard",
        "TikTokContent"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly FrayloDbContext _dbContext;

    public PromptTemplateService(FrayloDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <summary>
    /// Kiểm tra tính hợp lệ của Purpose.
    /// </summary>
    public static void ValidatePurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose) || !ValidPurposes.Contains(purpose))
        {
            throw new PromptValidationException(
                $"Mục đích prompt '{purpose}' không hợp lệ. Các mục đích cho phép: {string.Join(", ", ValidPurposes)}.");
        }
    }

    /// <summary>
    /// Lấy danh sách Prompt Templates.
    /// Prompt templates là tài nguyên toàn ứng dụng.
    /// </summary>
    public async Task<PromptListTemplatesResponseData> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var templates = await _dbContext.PromptTemplates
            .AsNoTracking()
            .OrderBy(t => t.Purpose)
            .ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);

        var dtos = templates.Select(ToDto).ToList();
        return new PromptListTemplatesResponseData(dtos);
    }

    /// <summary>
    /// Lấy chi tiết Prompt Template theo templateId hoặc purpose + resolution precedence.
    /// </summary>
    public async Task<PromptGetTemplateResponseData> GetTemplateAsync(
        string? templateId,
        string? purpose,
        CancellationToken cancellationToken = default)
    {
        PromptTemplate? template = null;

        if (!string.IsNullOrWhiteSpace(templateId))
        {
            template = await _dbContext.PromptTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken);

            if (template == null)
            {
                throw new PromptTemplateNotFoundException(templateId);
            }

        }
        else if (!string.IsNullOrWhiteSpace(purpose))
        {
            ValidatePurpose(purpose);

            template = await _dbContext.PromptTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Purpose == purpose && t.IsActive, cancellationToken);

            if (template == null)
            {
                throw new PromptTemplateNotFoundException(purpose);
            }
        }
        else
        {
            throw new PromptValidationException("Cần cung cấp ít nhất templateId hoặc purpose để lấy template.");
        }

        var templateDto = ToDto(template);
        var content = new PromptContentDto(template.Id, template.TemplateText, template.VariableDefinitionsJson, template.UpdatedAtUtc);
        return new PromptGetTemplateResponseData(templateDto, content);
    }

    /// <summary>
    /// Tạo hoặc cập nhật trực tiếp nội dung hiện hành của template.
    /// </summary>
    public async Task<PromptSaveTemplateResponseData> SaveTemplateAsync(
        PromptSaveTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePurpose(request.Purpose);

        if (string.IsNullOrWhiteSpace(request.TemplateText))
        {
            throw new PromptValidationException("Nội dung template không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new PromptValidationException("Tên mẫu prompt không được để trống.");
        }
        if (request.Name.Trim().Length > 120)
        {
            throw new PromptValidationException("Tên mẫu prompt không được vượt quá 120 ký tự.");
        }

        // Validate cú pháp template
        var (_, malformed) = PromptRenderer.ParseTemplate(request.TemplateText);
        if (malformed.Count > 0)
        {
            throw new PromptValidationException(
                $"Template chứa placeholder lỗi cú pháp: {string.Join(", ", malformed)}");
        }

        string nowUtc = DateTimeOffset.UtcNow.ToString("o");

        PromptTemplate template;
        if (string.IsNullOrWhiteSpace(request.TemplateId))
        {
            template = new PromptTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Purpose = request.Purpose,
                Name = request.Name.Trim(),
                IsActive = !await _dbContext.PromptTemplates.AnyAsync(t => t.Purpose == request.Purpose && t.IsActive, cancellationToken),
                TemplateText = request.TemplateText,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            };
            _dbContext.PromptTemplates.Add(template);
        }
        else
        {
            template = await _dbContext.PromptTemplates.FirstOrDefaultAsync(t => t.Id == request.TemplateId, cancellationToken)
                ?? throw new PromptTemplateNotFoundException(request.TemplateId);
            if (template.Purpose != request.Purpose)
            {
                throw new PromptValidationException("Mẫu prompt không thuộc mục đích đã chọn.");
            }
            template.Name = request.Name.Trim();
            template.TemplateText = request.TemplateText;
            template.UpdatedAtUtc = nowUtc;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        var templateDto = ToDto(template);
        var content = new PromptContentDto(template.Id, template.TemplateText, template.VariableDefinitionsJson, template.UpdatedAtUtc);
        return new PromptSaveTemplateResponseData(templateDto, content);
    }

    public async Task<PromptTemplateDto> ActivateTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            throw new PromptValidationException("Cần chọn mẫu prompt.");

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var selected = await _dbContext.PromptTemplates.FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken)
            ?? throw new PromptTemplateNotFoundException(templateId);
        if (!selected.IsActive)
        {
            var current = await _dbContext.PromptTemplates.FirstOrDefaultAsync(
                t => t.Purpose == selected.Purpose && t.IsActive, cancellationToken);
            if (current != null)
            {
                current.IsActive = false;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            selected.IsActive = true;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return ToDto(selected);
    }

    /// <summary>
    /// Thẩm định và Render Preview cho prompt template.
    /// Hỗ trợ cả preview văn bản nháp (editor draft) lẫn preview từ template đã lưu.
    /// </summary>
    public async Task<PromptPreviewResponseData> PreviewAsync(
        PromptPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string templateTextToRender;
        string? resolvedTemplateId = request.TemplateId;
        List<PromptVariableDefinition>? definitions = null;

        if (!string.IsNullOrWhiteSpace(request.TemplateText))
        {
            // Sử dụng văn bản nháp được truyền lên trực tiếp
            templateTextToRender = request.TemplateText;

            if (!string.IsNullOrWhiteSpace(request.TemplateId))
            {
                var template = await _dbContext.PromptTemplates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == request.TemplateId, cancellationToken);

                if (template != null)
                {
                    definitions = ParseDefinitions(template.VariableDefinitionsJson);
                }
            }
        }
        else
        {
            // Phân giải từ cơ sở dữ liệu
            var getResult = await GetTemplateAsync(
                request.TemplateId,
                request.Purpose,
                cancellationToken);

            var template = getResult.Template;
            var currentPrompt = getResult.Content;

            if (currentPrompt == null || string.IsNullOrWhiteSpace(currentPrompt.TemplateText))
            {
                throw new PromptValidationException("Template chưa có nội dung hoặc nội dung đang rỗng.");
            }

            templateTextToRender = currentPrompt.TemplateText;
            resolvedTemplateId = template.TemplateId;
            definitions = ParseDefinitions(currentPrompt.VariableDefinitionsJson);
        }

        string renderedText = PromptRenderer.Render(templateTextToRender, request.Variables, definitions);
        var (detectedVars, _) = PromptRenderer.ParseTemplate(templateTextToRender);

        return new PromptPreviewResponseData(
            RenderedText: renderedText,
            TemplateId: resolvedTemplateId,
            VariableCount: detectedVars.Count);
    }

    /// <summary>
    /// Tạo bản chụp bất biến (PromptRenderSnapshotDto) phục vụ cho T15 Job Queue.
    /// Bất biến hoàn toàn trước các thay đổi hoặc rollback template sau này.
    /// </summary>
    public async Task<PromptRenderSnapshotDto> CreateSnapshotAsync(
        string? templateId,
        string? purpose,
        IReadOnlyDictionary<string, string>? variables,
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewAsync(
            new PromptPreviewRequest(
                TemplateId: templateId,
                Purpose: purpose,
                TemplateText: null,
                Variables: variables),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(preview.TemplateId))
        {
            throw new PromptValidationException("Không thể tạo snapshot bất biến khi thiếu templateId.");
        }

        var snapshot = new PromptRenderSnapshot(
            templateId: preview.TemplateId,
            renderedText: preview.RenderedText,
            variables: variables,
            capturedAtUtc: DateTimeOffset.UtcNow.ToString("o"));

        return snapshot.ToDto();
    }

    private static List<PromptVariableDefinition>? ParseDefinitions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<PromptVariableDefinition>>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static PromptTemplateDto ToDto(PromptTemplate template)
    {
        return new PromptTemplateDto(
            TemplateId: template.Id,
            Purpose: template.Purpose,
            Name: template.Name,
            IsActive: template.IsActive,
            CreatedAtUtc: template.CreatedAtUtc,
            UpdatedAtUtc: template.UpdatedAtUtc);
    }
}
