using Fraylo.Contracts.Bridge;

namespace Fraylo.Desktop.Bridge;

public interface IBridgeDispatcher
{
    bool IsCommandAllowed(string command);

    void AllowCommand(string command);

    void RegisterHandler(string command, Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>> handler);

    bool UnregisterHandler(string command);

    Task<ResponseEnvelope> DispatchAsync(RequestEnvelope request, CancellationToken cancellationToken);
}
