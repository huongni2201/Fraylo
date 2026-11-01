using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Fraylo.Contracts.Bridge;

namespace Fraylo.Desktop.Bridge;

public sealed class BridgeMessageRouter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IBridgeDispatcher _dispatcher;
    private readonly CoreWebView2? _coreWebView2;
    private readonly Dispatcher? _uiDispatcher;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlightRequests = new();
    private readonly object _lifecycleLock = new();
    private readonly CancellationTokenSource _routerLifetimeCts = new();
    private volatile bool _isDisposed;
    private long _eventSequence;

    public BridgeMessageRouter(
        IBridgeDispatcher dispatcher,
        CoreWebView2? coreWebView2 = null,
        Dispatcher? uiDispatcher = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _coreWebView2 = coreWebView2;
        _uiDispatcher = uiDispatcher;
    }

    public static bool IsValidOrigin(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, "appassets.local", StringComparison.OrdinalIgnoreCase) &&
               uri.Port == 443;
    }

    public void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        string? source = e.Source;
        if (!IsValidOrigin(source))
        {
            // Từ chối thông điệp từ origin không hợp lệ và không xử lý tiếp
            return;
        }

        string? rawMessage = null;
        try
        {
            rawMessage = e.TryGetWebMessageAsString();
        }
        catch
        {
            // TryGetWebMessageAsString trả E_INVALIDARG khi message gửi từ JS là structured JSON object thay vì string.
            // Tiếp tục để đọc qua WebMessageAsJson bên dưới.
        }

        if (string.IsNullOrEmpty(rawMessage))
        {
            try
            {
                rawMessage = e.WebMessageAsJson;
            }
            catch
            {
                // Bỏ qua nếu không đọc được JSON object
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            if (_isDisposed)
            {
                return;
            }

            try
            {
                var response = await ProcessMessageAsync(source, rawMessage);
                if (response != null && !_isDisposed)
                {
                    await PostResponseAsync(response);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BridgeMessageRouter] Lỗi ngoài dự kiến: {ex.GetType().Name}");
            }
        });
    }

    public async Task<ResponseEnvelope?> ProcessMessageAsync(
        string? sourceUri,
        string rawMessage,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !IsValidOrigin(sourceUri))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return null;
        }

        int byteCount = Encoding.UTF8.GetByteCount(rawMessage);
        if (byteCount > BridgeContract.MaxEnvelopeBytes)
        {
            return ResponseEnvelopeHelper.CreateError(
                Guid.Empty,
                "INVALID_ARGUMENT",
                $"Bản tin có kích thước {byteCount} bytes, vượt quá giới hạn tối đa {BridgeContract.MaxEnvelopeBytes} bytes (4 MiB).",
                retryable: false,
                details: null);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawMessage);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Host Error] Lỗi phân tích cú pháp JSON envelope: {ex.GetType().Name}");
            return ResponseEnvelopeHelper.CreateError(
                Guid.Empty,
                "INVALID_ARGUMENT",
                "Lỗi phân tích cú pháp JSON envelope. Dữ liệu gửi lên không đúng định dạng JSON.",
                retryable: false,
                details: null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ResponseEnvelopeHelper.CreateError(
                    Guid.Empty,
                    "INVALID_ARGUMENT",
                    "Root JSON envelope phải là một JSON Object.",
                    retryable: false,
                    details: null);
            }

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            {
                return ResponseEnvelopeHelper.CreateError(
                    Guid.Empty,
                    "INVALID_ARGUMENT",
                    "Bản tin thiếu trường 'type' bắt buộc.",
                    retryable: false,
                    details: null);
            }

            string msgType = typeProp.GetString()!;

            Guid requestId = Guid.Empty;
            if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                _ = Guid.TryParse(idProp.GetString(), out requestId);
            }

            if (requestId == Guid.Empty)
            {
                return ResponseEnvelopeHelper.CreateError(
                    Guid.Empty,
                    "INVALID_ARGUMENT",
                    "Bản tin thiếu trường 'id' hoặc 'id' không phải là UUID/GUID hợp lệ.",
                    retryable: false,
                    details: null);
            }

            if (!root.TryGetProperty("contractVersion", out var versionProp) ||
                versionProp.ValueKind != JsonValueKind.String ||
                !string.Equals(versionProp.GetString(), BridgeContract.Version, StringComparison.Ordinal))
            {
                string givenVersion = versionProp.ValueKind == JsonValueKind.String ? versionProp.GetString() ?? "" : "";
                return ResponseEnvelopeHelper.CreateError(
                    requestId,
                    "INVALID_ARGUMENT",
                    $"Phiên bản hợp đồng '{givenVersion}' không được hỗ trợ. Bắt buộc phải là '{BridgeContract.Version}'.",
                    retryable: false,
                    details: null);
            }

            if (string.Equals(msgType, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                HandleCancelMessage(root);
                return null;
            }

            if (!string.Equals(msgType, "request", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseEnvelopeHelper.CreateError(
                    requestId,
                    "INVALID_ARGUMENT",
                    $"Loại bản tin '{msgType}' không được hỗ trợ. Chỉ hỗ trợ 'request' và 'cancel'.",
                    retryable: false,
                    details: null);
            }

            if (!root.TryGetProperty("command", out var cmdProp) ||
                cmdProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(cmdProp.GetString()))
            {
                return ResponseEnvelopeHelper.CreateError(
                    requestId,
                    "INVALID_ARGUMENT",
                    "Bản tin thiếu trường 'command' hoặc 'command' bị rỗng.",
                    retryable: false,
                    details: null);
            }

            string command = cmdProp.GetString()!;

            if (!root.TryGetProperty("payload", out var payloadProp))
            {
                return ResponseEnvelopeHelper.CreateError(
                    requestId,
                    "INVALID_ARGUMENT",
                    "Bản tin thiếu trường 'payload'.",
                    retryable: false,
                    details: null);
            }

            if (HasProhibitedSecurityFields(payloadProp))
            {
                return ResponseEnvelopeHelper.CreateError(
                    requestId,
                    "INVALID_ARGUMENT",
                    "Payload chứa trường không được phép (cookie, token, profile path, shell command hoặc file path tùy ý).",
                    retryable: false,
                    details: null);
            }

            DateTimeOffset clientTimestamp = DateTimeOffset.UtcNow;
            if (root.TryGetProperty("clientTimestampUtc", out var tsProp) && tsProp.ValueKind == JsonValueKind.String)
            {
                if (DateTimeOffset.TryParse(tsProp.GetString(), out var parsedTs))
                {
                    clientTimestamp = parsedTs;
                }
            }

            var requestEnvelope = new RequestEnvelope(
                Id: requestId,
                Type: "request",
                Command: command,
                Payload: payloadProp.Clone(),
                ClientTimestampUtc: clientTimestamp,
                ContractVersion: BridgeContract.Version
            );

            CancellationTokenSource cts;
            lock (_lifecycleLock)
            {
                if (_isDisposed)
                {
                    return null;
                }

                cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _routerLifetimeCts.Token);
                if (!_inFlightRequests.TryAdd(requestId, cts))
                {
                    cts.Dispose();
                    return ResponseEnvelopeHelper.CreateError(
                        requestId,
                        "CONCURRENCY_CONFLICT",
                        $"Yêu cầu với ID '{requestId}' đang được xử lý (trùng lặp request ID).",
                        retryable: true,
                        details: null);
                }
            }

            try
            {
                return await _dispatcher.DispatchAsync(requestEnvelope, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return ResponseEnvelopeHelper.CreateError(
                    requestId,
                    "INTERNAL_ERROR",
                    "Yêu cầu RPC trên Bridge đã bị hủy.",
                    retryable: false,
                    details: null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Host Error][CorrelationId={requestId}] Lỗi ngoại lệ loại '{ex.GetType().Name}' khi xử lý lệnh '{SanitizeForLog(command)}'");

                return ResponseEnvelopeHelper.CreateError(
                    requestId,
                    "INTERNAL_ERROR",
                    $"Đã xảy ra lỗi nội bộ ở máy chủ host khi xử lý yêu cầu. Mã đối soát (correlationId): {requestId:D}",
                    retryable: false,
                    details: null);
            }
            finally
            {
                _inFlightRequests.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(requestId, cts));
                cts.Dispose();
            }
        }
    }

    private void HandleCancelMessage(JsonElement root)
    {
        Guid targetRequestId = Guid.Empty;

        if (root.TryGetProperty("targetRequestId", out var targetProp) &&
            targetProp.ValueKind == JsonValueKind.String)
        {
            _ = Guid.TryParse(targetProp.GetString(), out targetRequestId);
        }
        else if (root.TryGetProperty("payload", out var payloadProp) &&
                 payloadProp.ValueKind == JsonValueKind.Object &&
                 payloadProp.TryGetProperty("targetRequestId", out var innerTarget) &&
                 innerTarget.ValueKind == JsonValueKind.String)
        {
            _ = Guid.TryParse(innerTarget.GetString(), out targetRequestId);
        }

        if (targetRequestId != Guid.Empty && _inFlightRequests.TryGetValue(targetRequestId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Request đã hoàn tất
            }
        }
    }

    private static bool HasProhibitedSecurityFields(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsProhibitedPropertyName(prop.Name))
                    {
                        return true;
                    }

                    if (HasProhibitedSecurityFields(prop.Value))
                    {
                        return true;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasProhibitedSecurityFields(item))
                    {
                        return true;
                    }
                }
                break;
        }

        return false;
    }

    private static bool IsProhibitedPropertyName(string name)
    {
        return name.Equals("cookie", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("cookies", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("tokens", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("accessToken", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("refreshToken", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("authToken", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bearerToken", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("profilePath", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("userDataDir", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("shellCommand", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("exec", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("sh", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    public async Task PostResponseAsync(ResponseEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_isDisposed || _coreWebView2 == null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(response, JsonOptions);

        if (_uiDispatcher != null && !_uiDispatcher.CheckAccess())
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!_isDisposed)
                    {
                        _coreWebView2.PostWebMessageAsJson(json);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Không thể gửi ResponseEnvelope: {ex.GetType().Name}");
                }
            });
        }
        else
        {
            try
            {
                _coreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Không thể gửi ResponseEnvelope: {ex.GetType().Name}");
            }
        }
    }

    public async Task PostEventAsync(string eventName, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(payload);

        if (_isDisposed || _coreWebView2 == null)
        {
            return;
        }

        long seq = Interlocked.Increment(ref _eventSequence);
        var evt = ResponseEnvelopeHelper.CreateEvent(eventName, seq, payload);
        string json = JsonSerializer.Serialize(evt, JsonOptions);

        if (_uiDispatcher != null && !_uiDispatcher.CheckAccess())
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!_isDisposed)
                    {
                        _coreWebView2.PostWebMessageAsJson(json);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Không thể gửi EventEnvelope: {ex.GetType().Name}");
                }
            });
        }
        else
        {
            try
            {
                _coreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Không thể gửi EventEnvelope: {ex.GetType().Name}");
            }
        }
    }

    public void Dispose()
    {
        List<CancellationTokenSource> inFlightToCancel;

        lock (_lifecycleLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            inFlightToCancel = new List<CancellationTokenSource>(_inFlightRequests.Values);
        }

        // Chạy Cancel ngoài critical section để tránh callback deadlock
        try
        {
            _routerLifetimeCts.Cancel();
        }
        catch (Exception)
        {
        }

        foreach (var cts in inFlightToCancel)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            catch (Exception) { }
        }

        // Không dispose _routerLifetimeCts hoặc cts của request tại đây để tránh ODE ngoài catch
        // CTS của request do finally của ProcessMessageAsync dọn dẹp an toàn.
    }
}
