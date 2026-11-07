using System.Text.Json;
using System.IO;
using Fraylo.Application.FacebookAnalytics;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.FacebookAnalytics;

namespace Fraylo.Desktop.Bridge.Handlers;

public sealed class FacebookAnalyticsHandlers(FacebookAnalyticsService service)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly string[] Commands = ["facebook.analytics.catalog", "facebook.analytics.pages.sync", "facebook.analytics.sync",
        "facebook.analytics.cancel", "facebook.analytics.dashboard", "facebook.analytics.history"];
    public void Register(IBridgeDispatcher dispatcher)
    {
        foreach (var command in Commands) { dispatcher.AllowCommand(command); dispatcher.RegisterHandler(command, HandleAsync); }
    }
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        foreach (var command in Commands) dispatcher.UnregisterHandler(command);
    }
    public async Task<ResponseEnvelope> HandleAsync(RequestEnvelope request, CancellationToken ct)
    {
        try
        {
            if (request.Command == "facebook.analytics.catalog") return ResponseEnvelopeHelper.CreateSuccess(request.Id, await service.CatalogAsync(ct));
            var input = request.Payload.Deserialize<FacebookAnalyticsRequest>(Json) ?? throw new ArgumentException("Thiếu dữ liệu Facebook Analytics.");
            object data = request.Command switch
            {
                "facebook.analytics.pages.sync" => await service.SyncPagesAsync(input, ct),
                "facebook.analytics.sync" => await service.StartAsync(input, ct),
                "facebook.analytics.cancel" => await CancelAsync(input, ct),
                "facebook.analytics.dashboard" => await service.DashboardAsync(input, ct),
                "facebook.analytics.history" => await service.HistoryAsync(input, ct),
                _ => throw new ArgumentException("Lệnh Facebook Analytics không hỗ trợ.")
            };
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, data);
        }
        catch (OperationCanceledException) { return ResponseEnvelopeHelper.CreateError(request.Id, "CANCELED", "Đã dừng yêu cầu Facebook Analytics.", false); }
        catch (Exception ex)
        {
            var message = ex is Microsoft.Playwright.PlaywrightException or TimeoutException ? "Facebook chưa tải được dữ liệu. Kiểm tra phiên và quyền Insights trong Cài đặt." :
                ex is IOException ? "Profile Facebook đang bị khóa hoặc chưa truy cập được. Hãy chờ thao tác khác hoàn tất rồi thử lại." :
                ex is Microsoft.Data.Sqlite.SqliteException ? "Chưa đọc hoặc lưu được cơ sở dữ liệu Analytics. Dữ liệu trước được giữ nguyên." : ex.Message;
            return ResponseEnvelopeHelper.CreateError(request.Id, ex is ArgumentException or JsonException ? "INVALID_ARGUMENT" : "FACEBOOK_ANALYTICS_BLOCKED", message, false);
        }
    }
    private async Task<object> CancelAsync(FacebookAnalyticsRequest input, CancellationToken ct) { await service.CancelAsync(input, ct); return new { }; }
}
