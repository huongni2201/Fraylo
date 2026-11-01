using System.Text.Json.Serialization;

namespace Fraylo.Application.Prompts.Models;

/// <summary>
/// Định nghĩa một biến số trong prompt template.
/// </summary>
public sealed record PromptVariableDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; init; } = true;

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; init; }

    [JsonPropertyName("example")]
    public string? Example { get; init; }

    public PromptVariableDefinition() { }

    public PromptVariableDefinition(
        string name,
        string? description = null,
        bool isRequired = true,
        string? defaultValue = null,
        string? example = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description;
        IsRequired = isRequired;
        DefaultValue = defaultValue;
        Example = example;
    }
}
