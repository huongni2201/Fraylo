using System.Collections.Concurrent;
using Fraylo.Contracts.Bridge;

namespace Fraylo.Desktop.Bridge;

public sealed class BridgeDispatcher : IBridgeDispatcher
{
    private static readonly HashSet<string> G1AllowedCommands = new(StringComparer.Ordinal)
    {
        "settings.get",
        "settings.save",
        "settings.browse_folder",
        "settings.validate_roots",
        "theme.get_preference",
        "theme.set_preference"
    };

    private readonly ConcurrentDictionary<string, byte> _allowedCommands =
        new(G1AllowedCommands.ToDictionary(c => c, _ => (byte)0), StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>>> _handlers =
        new(StringComparer.Ordinal);

    public bool IsCommandAllowed(string command)
    {
        return !string.IsNullOrWhiteSpace(command) && _allowedCommands.ContainsKey(command);
    }

    public void AllowCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        _allowedCommands[command] = 0;
    }

    public void RegisterHandler(string command, Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[command] = handler;
    }

    public bool UnregisterHandler(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return _handlers.TryRemove(command, out _);
    }

    public async Task<ResponseEnvelope> DispatchAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsCommandAllowed(request.Command))
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "UNKNOWN_COMMAND",
                $"Lệnh '{request.Command}' không nằm trong danh mục lệnh cho phép.",
                retryable: false,
                details: null);
        }

        if (!_handlers.TryGetValue(request.Command, out var handler))
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "INTERNAL_ERROR",
                $"Lệnh '{request.Command}' hiện chưa khả dụng.",
                retryable: false,
                details: null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await handler(request, cancellationToken);
    }
}
