using System.Text.Json;
using Fraylo.Contracts.Bridge;

namespace Fraylo.Desktop.Bridge;

public static class ResponseEnvelopeHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static ResponseEnvelope CreateSuccess(Guid id, object? data = null)
    {
        JsonElement? dataElement = null;
        if (data != null)
        {
            if (data is JsonElement element)
            {
                dataElement = element;
            }
            else
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
                using var doc = JsonDocument.Parse(bytes);
                dataElement = doc.RootElement.Clone();
            }
        }

        return new ResponseEnvelope(
            Id: id,
            Type: "response",
            Success: true,
            Data: dataElement,
            Error: null,
            ServerTimestampUtc: DateTimeOffset.UtcNow,
            ContractVersion: BridgeContract.Version
        );
    }

    public static ResponseEnvelope CreateError(
        Guid id,
        string code,
        string message,
        bool retryable = false,
        JsonElement? details = null)
    {
        var errorDto = new ErrorDto(
            Code: code,
            Message: message,
            Retryable: retryable,
            CorrelationId: id,
            Details: details
        );

        return new ResponseEnvelope(
            Id: id,
            Type: "response",
            Success: false,
            Data: null,
            Error: errorDto,
            ServerTimestampUtc: DateTimeOffset.UtcNow,
            ContractVersion: BridgeContract.Version
        );
    }

    public static EventEnvelope CreateEvent(
        string eventName,
        long sequence,
        object payload)
    {
        JsonElement payloadElement;
        if (payload is JsonElement element)
        {
            payloadElement = element;
        }
        else
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            using var doc = JsonDocument.Parse(bytes);
            payloadElement = doc.RootElement.Clone();
        }

        return new EventEnvelope(
            Id: Guid.NewGuid(),
            Type: "event",
            EventName: eventName,
            Sequence: sequence,
            Payload: payloadElement,
            ServerTimestampUtc: DateTimeOffset.UtcNow,
            ContractVersion: BridgeContract.Version
        );
    }
}
