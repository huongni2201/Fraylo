using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fraylo.Contracts.Bridge;

public sealed record RequestEnvelope(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("clientTimestampUtc")] DateTimeOffset ClientTimestampUtc,
    [property: JsonPropertyName("contractVersion")] string ContractVersion);
