using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Prompts;

public sealed record PromptTemplateDto(
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc);

public sealed record PromptContentDto(
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("templateText")] string TemplateText,
    [property: JsonPropertyName("variableDefinitionsJson")] string VariableDefinitionsJson,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc);

public sealed record PromptListTemplatesResponseData(
    [property: JsonPropertyName("templates")] IReadOnlyList<PromptTemplateDto> Templates);

public sealed record PromptGetTemplateRequest(
    [property: JsonPropertyName("templateId")] string? TemplateId,
    [property: JsonPropertyName("purpose")] string? Purpose);

public sealed record PromptGetTemplateResponseData(
    [property: JsonPropertyName("template")] PromptTemplateDto Template,
    [property: JsonPropertyName("content")] PromptContentDto Content);

public sealed record PromptSaveTemplateRequest(
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("templateText")] string TemplateText,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("templateId")] string? TemplateId);

public sealed record PromptActivateTemplateRequest(
    [property: JsonPropertyName("templateId")] string TemplateId);

public sealed record PromptSaveTemplateResponseData(
    [property: JsonPropertyName("template")] PromptTemplateDto Template,
    [property: JsonPropertyName("content")] PromptContentDto Content);

public sealed record PromptPreviewRequest(
    [property: JsonPropertyName("templateId")] string? TemplateId,
    [property: JsonPropertyName("purpose")] string? Purpose,
    [property: JsonPropertyName("templateText")] string? TemplateText,
    [property: JsonPropertyName("variables")] IReadOnlyDictionary<string, string>? Variables);

public sealed record PromptPreviewResponseData(
    [property: JsonPropertyName("renderedText")] string RenderedText,
    [property: JsonPropertyName("templateId")] string? TemplateId,
    [property: JsonPropertyName("variableCount")] int VariableCount);

public sealed record PromptRenderSnapshotDto(
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("renderedText")] string RenderedText,
    [property: JsonPropertyName("variables")] IReadOnlyDictionary<string, string> Variables,
    [property: JsonPropertyName("capturedAtUtc")] string CapturedAtUtc);
