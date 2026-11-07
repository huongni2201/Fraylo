using System.Text.Json;
using Fraylo.Application.Analytics;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Analytics;
using Fraylo.Contracts.Bridge;

namespace Fraylo.Desktop.Bridge.Handlers;

public sealed class AnalyticsAnalysisHandlers(AnalyticsAnalysisService service)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Commands = ["analytics.analysis.list", "analytics.analysis.get",
        "analytics.analysis.start", "analytics.analysis.retry", "analytics.analysis.cancel"];

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

    private async Task<ResponseEnvelope> HandleAsync(RequestEnvelope request, CancellationToken ct)
    {
        try
        {
            var input = request.Payload.Deserialize<AnalyticsAnalysisRequest>(Json) ?? throw new ArgumentException("Thiếu phạm vi Analytics.");
            var result = request.Command switch
            {
                "analytics.analysis.list" => await service.ListAsync(input, ct),
                "analytics.analysis.get" => await service.GetAsync(input, ct),
                "analytics.analysis.start" => await service.StartAsync(input, false, ct),
                "analytics.analysis.retry" => await service.StartAsync(input, true, ct),
                "analytics.analysis.cancel" => await service.CancelAsync(input, ct),
                _ => throw new ArgumentException("Lệnh phân tích không hợp lệ.")
            };
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException) { return ResponseEnvelopeHelper.CreateError(request.Id, "CANCELED", "Đã dừng yêu cầu.", false); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or JobValidationException or JobConcurrencyConflictException)
        {
            return ResponseEnvelopeHelper.CreateError(request.Id, "ANALYSIS_REQUEST_BLOCKED",
                ex is JsonException ? "Yêu cầu phân tích không hợp lệ." : ex.Message, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Analytics AI request failed: {0}", ex.GetType().Name);
            return ResponseEnvelopeHelper.CreateError(request.Id, "ANALYSIS_UNAVAILABLE", "Chưa xử lý được yêu cầu phân tích số liệu.", true);
        }
    }
}
