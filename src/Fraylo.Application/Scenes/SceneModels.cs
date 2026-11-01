namespace Fraylo.Application.Scenes;

/// <summary>
/// Ngoại lệ ném ra khi dữ liệu đầu vào hoặc thứ tự của Cảnh không thỏa mãn điều kiện hợp lệ.
/// </summary>
public class SceneValidationException : Exception
{
    public SceneValidationException(string message) : base(message) { }
    public SceneValidationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Ngoại lệ ném ra khi không tìm thấy bản ghi Cảnh tương ứng với mã yêu cầu.
/// </summary>
public class SceneNotFoundException : Exception
{
    public SceneNotFoundException(string sceneId)
        : base($"Không tìm thấy cảnh với mã '{sceneId}'.") { }
    public SceneNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Ngoại lệ ném ra khi thao tác với Cảnh gặp xung đột đồng thời hoặc vi phạm guard nghiệp vụ.
/// </summary>
public class SceneConcurrencyConflictException : Exception
{
    public SceneConcurrencyConflictException(string message) : base(message) { }
    public SceneConcurrencyConflictException(string message, Exception innerException) : base(message, innerException) { }
}
