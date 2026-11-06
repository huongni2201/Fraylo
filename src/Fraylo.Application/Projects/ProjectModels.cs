namespace Fraylo.Application.Projects;

/// <summary>
/// Ngoại lệ ném ra khi dữ liệu đầu vào của Dự án không thỏa mãn điều kiện hợp lệ.
/// </summary>
public class ProjectValidationException : Exception
{
    public ProjectValidationException(string message) : base(message) { }
    public ProjectValidationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Ngoại lệ ném ra khi không tìm thấy bản ghi Dự án tương ứng với mã yêu cầu.
/// </summary>
public class ProjectNotFoundException : Exception
{
    public ProjectNotFoundException(string projectId)
        : base($"Không tìm thấy dự án với mã '{projectId}'.") { }
    public ProjectNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Ngoại lệ ném ra khi thao tác với Dự án gặp xung đột đồng thời hoặc vi phạm guard nghiệp vụ.
/// </summary>
public class ProjectConcurrencyConflictException : Exception
{
    public ProjectConcurrencyConflictException(string message) : base(message) { }
    public ProjectConcurrencyConflictException(string message, Exception innerException) : base(message, innerException) { }
}
