namespace Fraylo.Application.Prompts.Exceptions;

/// <summary>
/// Ngoại lệ cơ sở cho các lỗi trong Prompt Engine.
/// </summary>
public abstract class PromptEngineException : Exception
{
    protected PromptEngineException(string message) : base(message) { }
    protected PromptEngineException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Ngoại lệ khi không tìm thấy template.
/// </summary>
public class PromptTemplateNotFoundException : PromptEngineException
{
    public string? TemplateId { get; }

    public PromptTemplateNotFoundException(string templateId)
        : base($"Không tìm thấy Prompt Template với ID '{templateId}'.")
    {
        TemplateId = templateId;
    }

}

/// <summary>
/// Ngoại lệ khi template text hoặc biến số vi phạm quy tắc thẩm định cú pháp hoặc dữ liệu.
/// </summary>
public class PromptValidationException : PromptEngineException
{
    public IReadOnlyList<string> Errors { get; }

    public PromptValidationException(string message, IReadOnlyList<string>? errors = null)
        : base(message)
    {
        Errors = errors ?? Array.Empty<string>();
    }
}
