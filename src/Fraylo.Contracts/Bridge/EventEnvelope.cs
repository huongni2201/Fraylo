using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Bridge;

public sealed record EventEnvelope(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("eventName")] string EventName,
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("serverTimestampUtc")] DateTimeOffset ServerTimestampUtc,
    [property: JsonPropertyName("contractVersion")] string ContractVersion);
