using System.Text.Json;
using Fraylo.Application.Publishing;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Publishing;

namespace Fraylo.Desktop.Bridge.Handlers;

public sealed class PublishingHandlers(TikTokPublishingService service)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static readonly string[] Commands = ["publishing.list", "publishing.publish"];

    public void Register(IBridgeDispatcher dispatcher)
    {
        foreach (var command in Commands)
        {
            dispatcher.AllowCommand(command);
            dispatcher.RegisterHandler(command, HandleAsync);
        }
    }

    public void Unregister(IBridgeDispatcher dispatcher)
    {
        foreach (var command in Commands) dispatcher.UnregisterHandler(command);
    }

    public async Task<ResponseEnvelope> HandleAsync(RequestEnvelope request, CancellationToken ct)
    {
        try
        {
            object result = request.Command switch
            {
                "publishing.list" => await service.ListAsync(ct),
                "publishing.publish" => await service.PublishAsync(Parse<PublishVideoRequest>(request), ct),
                _ => throw new ArgumentException("Lệnh đăng video không được hỗ trợ.")
            };
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var code = ex is ArgumentException or JsonException ? "INVALID_ARGUMENT" :
                ex is KeyNotFoundException ? "ENTITY_NOT_FOUND" : "PUBLISH_BLOCKED";
            return ResponseEnvelopeHelper.CreateError(request.Id, code, ex.Message, retryable: false);
        }
    }

    private static T Parse<T>(RequestEnvelope request) => request.Payload.Deserialize<T>(JsonOptions)
        ?? throw new ArgumentException("Thiếu dữ liệu yêu cầu.");
}
