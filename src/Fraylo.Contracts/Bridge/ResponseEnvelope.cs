using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Bridge;

public sealed record ResponseEnvelope(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("error")] ErrorDto? Error,
    [property: JsonPropertyName("serverTimestampUtc")] DateTimeOffset ServerTimestampUtc,
    [property: JsonPropertyName("contractVersion")] string ContractVersion);
