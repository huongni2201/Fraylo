namespace Fraylo.Infrastructure.Persistence.Entities;

public class PromptTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string TemplateText { get; set; } = string.Empty;
    public string VariableDefinitionsJson { get; set; } = "[]";
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;

}
