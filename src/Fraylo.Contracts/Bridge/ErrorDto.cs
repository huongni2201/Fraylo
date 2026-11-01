using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Bridge;

public sealed record ErrorDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("correlationId")] Guid CorrelationId,
    [property: JsonPropertyName("details")] JsonElement? Details);
