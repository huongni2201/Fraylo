namespace Fraylo.Infrastructure.Persistence.Entities;

public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;
}
