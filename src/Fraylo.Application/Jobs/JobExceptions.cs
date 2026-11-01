using System;

namespace Fraylo.Application.Jobs;

/// <summary>
/// Ngoại lệ ném ra khi không tìm thấy tác vụ (Job) theo ID yêu cầu.
/// Mã lỗi chuẩn: NOT_FOUND.
/// </summary>
public sealed class JobNotFoundException : Exception
{
    public string JobId { get; }
    public string ErrorCode => "NOT_FOUND";

    public JobNotFoundException(string jobId)
        : base($"Không tìm thấy tác vụ với mã định danh '{jobId}'.")
    {
        JobId = jobId;
    }
}

/// <summary>
/// Ngoại lệ ném ra khi dữ liệu đầu vào của tác vụ không hợp lệ.
/// Mã lỗi chuẩn: INVALID_ARGUMENT.
/// </summary>
public sealed class JobValidationException : Exception
{
    public string? Field { get; }
    public string ErrorCode => "INVALID_ARGUMENT";

    public JobValidationException(string message, string? field = null)
        : base(message)
    {
        Field = field;
    }
}

/// <summary>
/// Ngoại lệ ném ra khi nhà cung cấp AI cho loại tác vụ chưa được cấu hình trong Settings.
/// Tuân thủ SETTINGS_PLAN.md §5.4: Không bao giờ fallback ngầm khi chưa chọn provider.
/// Mã lỗi chuẩn: PROVIDER_NOT_CONFIGURED.
/// </summary>
public sealed class ProviderNotConfiguredException : Exception
{
    public string TaskType { get; }
    public string ErrorCode => "PROVIDER_NOT_CONFIGURED";

    public ProviderNotConfiguredException(string taskType)
        : base($"Chưa cấu hình nhà cung cấp AI cho tác vụ '{taskType}'. Vui lòng mở Cài đặt để chọn nhà cung cấp trước khi tiếp tục.")
    {
        TaskType = taskType;
    }
}

/// <summary>
/// Ngoại lệ ném ra khi thư mục gốc lưu trữ (Storage Root) không khả dụng hoặc chưa cấu hình.
/// Mã lỗi chuẩn: STORAGE_ROOT_UNAVAILABLE.
/// </summary>
public sealed class StorageRootUnavailableException : Exception
{
    public string StorageRootId { get; }
    public string ErrorCode => "STORAGE_ROOT_UNAVAILABLE";

    public StorageRootUnavailableException(string storageRootId)
        : base($"Thư mục lưu trữ gốc '{storageRootId}' không khả dụng hoặc chưa được định cấu hình.")
    {
        StorageRootId = storageRootId;
    }
}

/// <summary>
/// Ngoại lệ ném ra khi worker cũ cố gắng cập nhật CSDL (heartbeat hoặc commit kết quả)
/// nhưng fencing token không khớp (đã bị vô hiệu hóa bởi worker mới).
/// Chặn đứng hoàn toàn Zombie Worker.
/// Mã lỗi chuẩn: FENCING_TOKEN_STALE.
/// </summary>
public sealed class FencingTokenStaleException : Exception
{
    public string JobId { get; }
    public long ExpectedToken { get; }
    public string ErrorCode => "FENCING_TOKEN_STALE";

    public FencingTokenStaleException(string jobId, long expectedToken)
        : base($"Fencing token '{expectedToken}' đã bị vô hiệu hóa trên CSDL cho tác vụ '{jobId}'. Thao tác bị từ chối để bảo vệ an toàn dữ liệu.")
    {
        JobId = jobId;
        ExpectedToken = expectedToken;
    }
}

/// <summary>
/// Ngoại lệ ném ra khi xảy ra xung đột trạng thái tác vụ trong CSDL
/// (ví dụ: cố gắng cancel một tác vụ đã hoàn thành, hoặc cố gắng reconcile tác vụ không ở trạng thái AwaitingUser).
/// Mã lỗi chuẩn: CONCURRENCY_CONFLICT.
/// </summary>
public sealed class JobConcurrencyConflictException : Exception
{
    public string? JobId { get; }
    public string ErrorCode => "CONCURRENCY_CONFLICT";

    public JobConcurrencyConflictException(string message, string? jobId = null)
        : base(message)
    {
        JobId = jobId;
    }
}
