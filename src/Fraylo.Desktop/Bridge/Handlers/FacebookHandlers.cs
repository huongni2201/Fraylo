using System.Text.Json;
using Microsoft.Playwright;
using Fraylo.Application.Facebook;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Facebook;

namespace Fraylo.Desktop.Bridge.Handlers;

public sealed class FacebookHandlers(FacebookPublishingService service)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static readonly string[] Commands = ["facebook.list", "facebook.groups.sync", "facebook.tasks.create", "facebook.tasks.cancel", "facebook.tasks.resume",
        "facebook.tasks.post_now", "facebook.tasks.reschedule", "facebook.drafts.save", "facebook.drafts.delete"];

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
                "facebook.list" => await service.ListAsync(ct),
                "facebook.groups.sync" => await service.SyncGroupsAsync(Parse<FacebookSyncRequest>(request), ct),
                "facebook.tasks.create" => await service.CreateBatchAsync(Parse<FacebookCreateBatchRequest>(request), ct),
                "facebook.tasks.cancel" => await service.CancelAsync(Parse<FacebookTaskRequest>(request), ct),
                "facebook.tasks.resume" => await service.ResumeAsync(Parse<FacebookTaskRequest>(request), ct),
                "facebook.tasks.post_now" => await service.PostNowAsync(Parse<FacebookTaskRequest>(request), ct),
                "facebook.tasks.reschedule" => await service.RescheduleAsync(Parse<FacebookRescheduleRequest>(request), ct),
                "facebook.drafts.save" => await service.SaveDraftAsync(Parse<FacebookSaveDraftRequest>(request), ct),
                "facebook.drafts.delete" => await DeleteDraftAsync(Parse<FacebookDraftRequest>(request), ct),
                _ => throw new ArgumentException("Lệnh Facebook không được hỗ trợ.")
            };
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var code = ex is ArgumentException or JsonException ? "INVALID_ARGUMENT" :
                ex is KeyNotFoundException ? "ENTITY_NOT_FOUND" : "FACEBOOK_BLOCKED";
            var message = ex is PlaywrightException or TimeoutException
                ? "Không thể hoàn tất thao tác Facebook. Kiểm tra kết nối và phiên tài khoản rồi tải lại; ứng dụng không tự đăng lại."
                : ex.Message;
            return ResponseEnvelopeHelper.CreateError(request.Id, code, message, retryable: false);
        }
    }

    private async Task<object> DeleteDraftAsync(FacebookDraftRequest request, CancellationToken ct)
    {
        await service.DeleteDraftAsync(request, ct);
        return new { };
    }

    private static T Parse<T>(RequestEnvelope request) => request.Payload.Deserialize<T>(JsonOptions)
        ?? throw new ArgumentException("Thiếu dữ liệu yêu cầu Facebook.");
}
