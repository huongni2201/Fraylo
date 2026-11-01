using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Analytics;
using Fraylo.Contracts.Analytics;
using Fraylo.Contracts.Bridge;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối các lệnh phân tích dữ liệu TikTok & Affiliate (analytics.*)
/// theo đặc tả Task T25 và hợp đồng RPC Bridge.
/// </summary>
public sealed class AnalyticsHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAnalyticsService _analyticsService;
    private ITikTokAnalyticsCrawler? _tiktokCrawler;

    public AnalyticsHandlers(IAnalyticsService analyticsService, ITikTokAnalyticsCrawler? tiktokCrawler = null)
    {
        _analyticsService = analyticsService ?? throw new ArgumentNullException(nameof(analyticsService));
        _tiktokCrawler = tiktokCrawler;
    }

    /// <summary>
    /// Gán hoặc thay đổi crawler TikTok Analytics động sau khi khởi tạo.
    /// </summary>
    public void SetTikTokCrawler(ITikTokAnalyticsCrawler? crawler)
    {
        _tiktokCrawler = crawler;
    }

    /// <summary>
    /// Đăng ký toàn bộ danh mục lệnh analytics.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("analytics.get_summary");
        dispatcher.AllowCommand("analytics.get_timeseries");
        dispatcher.AllowCommand("analytics.list_top_videos");
        dispatcher.AllowCommand("analytics.list_tiktok_accounts");
        dispatcher.AllowCommand("analytics.import_data");
        dispatcher.AllowCommand("analytics.export_data");
        dispatcher.AllowCommand("analytics.check_tiktok_session");
        dispatcher.AllowCommand("analytics.open_tiktok_login");
        dispatcher.AllowCommand("analytics.sync_tiktok_channel");

        dispatcher.RegisterHandler("analytics.get_summary", HandleGetSummaryAsync);
        dispatcher.RegisterHandler("analytics.get_timeseries", HandleGetTimeseriesAsync);
        dispatcher.RegisterHandler("analytics.list_top_videos", HandleListTopVideosAsync);
        dispatcher.RegisterHandler("analytics.list_tiktok_accounts", HandleListTikTokAccountsAsync);
        dispatcher.RegisterHandler("analytics.import_data", HandleImportDataAsync);
        dispatcher.RegisterHandler("analytics.export_data", HandleExportDataAsync);
        dispatcher.RegisterHandler("analytics.check_tiktok_session", HandleCheckTikTokSessionAsync);
        dispatcher.RegisterHandler("analytics.open_tiktok_login", HandleOpenTikTokLoginAsync);
        dispatcher.RegisterHandler("analytics.sync_tiktok_channel", HandleSyncTikTokChannelAsync);
    }

    /// <summary>
    /// Hủy đăng ký danh mục lệnh analytics.* khỏi BridgeDispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("analytics.get_summary");
        dispatcher.UnregisterHandler("analytics.get_timeseries");
        dispatcher.UnregisterHandler("analytics.list_top_videos");
        dispatcher.UnregisterHandler("analytics.list_tiktok_accounts");
        dispatcher.UnregisterHandler("analytics.import_data");
        dispatcher.UnregisterHandler("analytics.export_data");
        dispatcher.UnregisterHandler("analytics.check_tiktok_session");
        dispatcher.UnregisterHandler("analytics.open_tiktok_login");
        dispatcher.UnregisterHandler("analytics.sync_tiktok_channel");
    }

    /// <summary>
    /// Xử lý RPC analytics.get_summary
    /// </summary>
    public async Task<ResponseEnvelope> HandleGetSummaryAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<GetAnalyticsSummaryRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccountId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Vui lòng chọn tài khoản TikTok.",
                    retryable: false);
            }

            var summary = await _analyticsService.GetSummaryAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý RPC analytics.get_timeseries
    /// </summary>
    public async Task<ResponseEnvelope> HandleGetTimeseriesAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<GetAnalyticsTimeseriesRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccountId) || string.IsNullOrWhiteSpace(payload.Metric))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload cần tài khoản TikTok và metric.",
                    retryable: false);
            }

            var points = await _analyticsService.GetTimeseriesAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new { points });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý RPC analytics.list_top_videos
    /// </summary>
    public async Task<ResponseEnvelope> HandleListTopVideosAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ListTopVideosRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccountId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Vui lòng chọn tài khoản TikTok.",
                    retryable: false);
            }

            var videos = await _analyticsService.ListTopVideosAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new { videos });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleListTikTokAccountsAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var accounts = await _analyticsService.ListTikTokAccountsAsync(cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, new TikTokAnalyticsAccountsResponseData(accounts));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý RPC analytics.import_data
    /// </summary>
    public async Task<ResponseEnvelope> HandleImportDataAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ImportAnalyticsDataRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.AccountId) ||
                string.IsNullOrWhiteSpace(payload.Format) ||
                string.IsNullOrWhiteSpace(payload.RawContent))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload cần tài khoản TikTok, format và rawContent.",
                    retryable: false);
            }

            var result = await _analyticsService.ImportDataAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý RPC analytics.export_data
    /// </summary>
    public async Task<ResponseEnvelope> HandleExportDataAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ExportAnalyticsDataRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccountId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Vui lòng chọn tài khoản TikTok.",
                    retryable: false);
            }

            var result = await _analyticsService.ExportDataAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý RPC analytics.check_tiktok_session
    /// </summary>
    public async Task<ResponseEnvelope> HandleCheckTikTokSessionAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_tiktokCrawler == null)
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "NOT_SUPPORTED",
                "Dịch vụ TikTok Analytics Crawler chưa được khởi tạo trên hệ thống.",
                retryable: false);
        }

        try
        {
            var payload = ParsePayload<TikTokCheckSessionRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccountId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Vui lòng chọn tài khoản TikTok.",
                    retryable: false);
            }

            var result = await _tiktokCrawler.CheckSessionAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý RPC analytics.open_tiktok_login
    /// </summary>
    public async Task<ResponseEnvelope> HandleOpenTikTokLoginAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_tiktokCrawler == null)
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "NOT_SUPPORTED",
                "Dịch vụ TikTok Analytics Crawler chưa được khởi tạo trên hệ thống.",
                retryable: false);
        }

        try
        {
            var payload = ParsePayload<TikTokOpenLoginRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccountId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Vui lòng chọn tài khoản TikTok.",
                    retryable: false);
            }

            var result = await _tiktokCrawler.OpenLoginAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    /// <summary>
    /// Xử lý RPC analytics.sync_tiktok_channel
    /// </summary>
    public async Task<ResponseEnvelope> HandleSyncTikTokChannelAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_tiktokCrawler == null)
        {
            return ResponseEnvelopeHelper.CreateError(
                request.Id,
                "NOT_SUPPORTED",
                "Dịch vụ TikTok Analytics Crawler chưa được khởi tạo trên hệ thống.",
                retryable: false);
        }

        try
        {
            var payload = ParsePayload<TikTokSyncChannelRequest>(request);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccountId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Vui lòng chọn tài khoản TikTok.",
                    retryable: false);
            }

            var result = await _tiktokCrawler.SyncChannelDataAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    private static T? ParsePayload<T>(RequestEnvelope request) where T : class
    {
        var element = request.Payload;
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
    }

    private static ResponseEnvelope MapExceptionToError(Guid requestId, Exception ex)
    {
        return ex switch
        {
            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            KeyNotFoundException notFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", notFound.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xử lý phân tích: {ex.Message}", retryable: false)
        };
    }
}
