namespace Fraylo.Infrastructure.Persistence.Entities;

public class Project
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Brief { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft"; // Draft | InProgress | Completed
    public string? CurrentVideoRevisionId { get; set; }
    public string? GroupId { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;

}
