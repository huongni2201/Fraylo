namespace Fraylo.Application.Prompts.Models;

/// <summary>
/// Kết quả thẩm định cú pháp template và biến số.
/// </summary>
public sealed record PromptValidationResult
{
    public bool IsValid => Errors.Count == 0 &&
                           MissingVariables.Count == 0 &&
                           MalformedPlaceholders.Count == 0;

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingVariables { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UndeclaredVariables { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MalformedPlaceholders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DetectedVariables { get; init; } = Array.Empty<string>();

    public static PromptValidationResult Success(IReadOnlyList<string> detectedVariables) =>
        new()
        {
            DetectedVariables = detectedVariables
        };

    public static PromptValidationResult Failure(
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? missingVariables = null,
        IReadOnlyList<string>? undeclaredVariables = null,
        IReadOnlyList<string>? malformedPlaceholders = null,
        IReadOnlyList<string>? detectedVariables = null) =>
        new()
        {
            Errors = errors ?? Array.Empty<string>(),
            MissingVariables = missingVariables ?? Array.Empty<string>(),
            UndeclaredVariables = undeclaredVariables ?? Array.Empty<string>(),
            MalformedPlaceholders = malformedPlaceholders ?? Array.Empty<string>(),
            DetectedVariables = detectedVariables ?? Array.Empty<string>()
        };
}
