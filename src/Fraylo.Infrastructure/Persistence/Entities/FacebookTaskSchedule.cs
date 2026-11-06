namespace Fraylo.Infrastructure.Persistence.Entities;

/// <summary>Giờ hẹn của một tác vụ; bảng riêng để nâng cấp schema chỉ cộng thêm bảng, không sửa FacebookPostTask.</summary>
public sealed class FacebookTaskSchedule
{
    public string TaskId { get; set; } = string.Empty;
    public string DueAtUtc { get; set; } = string.Empty;
}
