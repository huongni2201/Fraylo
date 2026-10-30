using System.Net.Http;
using TiToAI.Contracts.Bridge;
using TiToAI.Desktop.Hosting;

namespace TiToAI.Desktop.Bridge.Handlers;

public sealed class UpdateHandlers(AppUpdateService service)
{
    public static readonly string[] Commands = ["updates.check", "updates.apply"];

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
            if (request.Command == "updates.apply")
            {
                await service.ApplyAsync(ct);
                return ResponseEnvelopeHelper.CreateSuccess(request.Id, new { started = true });
            }
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, await service.CheckAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var code = ex is HttpRequestException ? "UPDATE_UNREACHABLE" : "UPDATE_FAILED";
            return ResponseEnvelopeHelper.CreateError(request.Id, code, ex.Message, retryable: ex is HttpRequestException);
        }
    }
}
