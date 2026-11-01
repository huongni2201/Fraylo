using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Fraylo.Contracts.Bridge;
using Fraylo.Desktop.Bridge;
using Fraylo.Infrastructure.Settings;

namespace Fraylo.Desktop.Hosting;

public sealed class HostThemeService : IHostThemeService
{
    private static readonly System.Windows.Media.Color DarkBackgroundColor =
        System.Windows.Media.Color.FromRgb(0x11, 0x0D, 0x0C); // #110D0C

    private static readonly System.Windows.Media.Color LightBackgroundColor =
        System.Windows.Media.Color.FromRgb(0xFA, 0xF6, 0xF3); // #FAF6F3

    private static readonly System.Drawing.Color DarkDrawingColor =
        System.Drawing.Color.FromArgb(255, 0x11, 0x0D, 0x0C);

    private static readonly System.Drawing.Color LightDrawingColor =
        System.Drawing.Color.FromArgb(255, 0xFA, 0xF6, 0xF3);

    private Window? _window;
    private WebView2? _webView;
    private Func<BridgeMessageRouter?>? _getRouter;
    private readonly ThemePreferenceStore? _preferenceStore;
    private bool _systemThemeMonitorActive;
    private bool _disposed;
    private volatile bool _preferenceReadFailed;

    public string CurrentPreference { get; private set; } = "system";

    public string CurrentEffectiveTheme { get; private set; } = "light";

    public event Action<string, string>? ThemeChanged;

    public HostThemeService(ThemePreferenceStore? preferenceStore = null)
    {
        _preferenceStore = preferenceStore;
        CurrentPreference = "system";
        CurrentEffectiveTheme = GetEffectiveTheme(CurrentPreference);
    }

    public async Task InitializePreferenceAsync(CancellationToken cancellationToken)
    {
        if (_preferenceStore != null)
        {
            try
            {
                var storedPreference = await _preferenceStore.GetAsync(cancellationToken).ConfigureAwait(false);
                _preferenceReadFailed = false;
                ApplyPreference(
                    IsValidPreference(storedPreference) ? storedPreference! : "system",
                    _window,
                    _webView,
                    notify: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _preferenceReadFailed = true;
                System.Diagnostics.Debug.WriteLine($"[HostThemeService] Could not read ui.theme: {ex.GetType().Name}");
                ApplyPreference("system", _window, _webView, notify: false);
            }
        }

        StartSystemThemeMonitor();
    }

    public static string ResolveWindowsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                if (key.GetValue("AppsUseLightTheme") is int appsUseLight)
                {
                    return appsUseLight == 0 ? "dark" : "light";
                }

                if (key.GetValue("SystemUsesLightTheme") is int sysUseLight)
                {
                    return sysUseLight == 0 ? "dark" : "light";
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HostThemeService] Could not read Windows theme: {ex.GetType().Name}");
        }

        // Contract §6.1 keeps the System preference and falls back to dark if Windows is unreadable.
        return "dark";
    }

    public static bool DetectIsWindowsInDarkMode()
    {
        return string.Equals(ResolveWindowsTheme(), "dark", StringComparison.OrdinalIgnoreCase);
    }

    public string GetEffectiveTheme(string preference)
    {
        if (string.Equals(preference, "light", StringComparison.OrdinalIgnoreCase))
        {
            return "light";
        }

        if (string.Equals(preference, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return "dark";
        }

        // Với "system" hoặc giá trị không xác định: tính từ Windows mà không đổi preference
        return ResolveWindowsTheme();
    }

    public void ApplyTheme(string effectiveTheme, Window? window, WebView2? webView)
    {
        if (window != null)
        {
            _window = window;
        }

        if (webView != null)
        {
            _webView = webView;
        }

        CurrentEffectiveTheme = string.Equals(effectiveTheme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";

        bool isDark = CurrentEffectiveTheme == "dark";

        if (_window != null)
        {
            void ApplyToWindow()
            {
                var brushColor = isDark ? DarkBackgroundColor : LightBackgroundColor;
                _window.Background = new SolidColorBrush(brushColor);
            }

            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.Invoke(ApplyToWindow);
            }
            else
            {
                ApplyToWindow();
            }
        }

        if (_webView != null)
        {
            void ApplyToWebView()
            {
                _webView.DefaultBackgroundColor = isDark ? DarkDrawingColor : LightDrawingColor;
            }

            if (!_webView.Dispatcher.CheckAccess())
            {
                _webView.Dispatcher.Invoke(ApplyToWebView);
            }
            else
            {
                ApplyToWebView();
            }
        }
    }

    public void SetPreference(string preference, Window? window, WebView2? webView)
    {
        ApplyPreference(preference, window, webView, notify: true);
    }

    public void RegisterBridgeHandlers(IBridgeDispatcher dispatcher, Func<BridgeMessageRouter?> getRouter)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _getRouter = getRouter;

        dispatcher.RegisterHandler("theme.get_preference", async (request, ct) =>
        {
            if (_preferenceReadFailed && _preferenceStore != null)
            {
                try
                {
                    var storedPreference = await _preferenceStore.GetAsync(ct).ConfigureAwait(false);
                    _preferenceReadFailed = false;
                    ApplyPreference(
                        IsValidPreference(storedPreference) ? storedPreference! : "system",
                        _window,
                        _webView,
                        notify: true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HostThemeService] Could not retry ui.theme read: {ex.GetType().Name}");
                    return CreatePersistenceError(
                        request.Id,
                        "Không đọc được giao diện đã lưu. Ứng dụng đang dùng Theo hệ thống; hãy thử lại trong Cài đặt.");
                }
            }

            var data = new
            {
                preference = CurrentPreference,
                effectiveTheme = CurrentEffectiveTheme
            };

            return ResponseEnvelopeHelper.CreateSuccess(request.Id, data);
        });

        dispatcher.RegisterHandler("theme.set_preference", async (request, ct) =>
        {
            string? newPref = null;
            if (request.Payload.ValueKind == JsonValueKind.Object &&
                request.Payload.TryGetProperty("preference", out var prefProp) &&
                prefProp.ValueKind == JsonValueKind.String)
            {
                newPref = prefProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(newPref) || !IsValidPreference(newPref))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Trường 'preference' không hợp lệ. Phải là 'system', 'light', hoặc 'dark'.",
                    retryable: false,
                    details: null);
            }

            SetPreference(newPref, _window, _webView);

            if (_preferenceStore == null)
            {
                return CreatePersistenceError(request.Id, "Không thể lưu giao diện trong phiên hiện tại.");
            }

            try
            {
                await _preferenceStore.SetAsync(CurrentPreference, ct).ConfigureAwait(false);
                _preferenceReadFailed = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HostThemeService] Could not save ui.theme: {ex.GetType().Name}");
                return CreatePersistenceError(
                    request.Id,
                    "Giao diện đã áp dụng trong phiên này nhưng chưa lưu được. Hãy thử lại.");
            }

            var data = new
            {
                preference = CurrentPreference,
                effectiveTheme = CurrentEffectiveTheme
            };

            return ResponseEnvelopeHelper.CreateSuccess(request.Id, data);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_systemThemeMonitorActive)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _systemThemeMonitorActive = false;
        }
    }

    private void ApplyPreference(string preference, Window? window, WebView2? webView, bool notify)
    {
        CurrentPreference = IsValidPreference(preference) ? preference.ToLowerInvariant() : "system";
        ApplyTheme(GetEffectiveTheme(CurrentPreference), window, webView);

        if (!notify)
        {
            return;
        }

        ThemeChanged?.Invoke(CurrentPreference, CurrentEffectiveTheme);
        var router = _getRouter?.Invoke();
        if (router != null)
        {
            _ = router.PostEventAsync("theme.changed", new
            {
                preference = CurrentPreference,
                effectiveTheme = CurrentEffectiveTheme
            });
        }
    }

    private ResponseEnvelope CreatePersistenceError(Guid requestId, string message)
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            preference = CurrentPreference,
            effectiveTheme = CurrentEffectiveTheme
        });

        return ResponseEnvelopeHelper.CreateError(
            requestId,
            "INTERNAL_ERROR",
            message,
            retryable: true,
            details);
    }

    private void StartSystemThemeMonitor()
    {
        if (_systemThemeMonitorActive || _disposed)
        {
            return;
        }

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _systemThemeMonitorActive = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HostThemeService] Could not monitor Windows theme: {ex.GetType().Name}");
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        var dispatcher = _window?.Dispatcher;
        if (_disposed || dispatcher == null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                ApplyCurrentSystemTheme();
            }
            else
            {
                _ = dispatcher.BeginInvoke(new Action(ApplyCurrentSystemTheme));
            }
        }
        catch (InvalidOperationException)
        {
            // The WPF dispatcher is shutting down.
        }
    }

    private void ApplyCurrentSystemTheme()
    {
        if (_disposed || !string.Equals(CurrentPreference, "system", StringComparison.Ordinal))
        {
            return;
        }

        var effectiveTheme = GetEffectiveTheme(CurrentPreference);
        if (string.Equals(effectiveTheme, CurrentEffectiveTheme, StringComparison.Ordinal))
        {
            return;
        }

        ApplyTheme(effectiveTheme, _window, _webView);
        ThemeChanged?.Invoke(CurrentPreference, CurrentEffectiveTheme);

        var router = _getRouter?.Invoke();
        if (router != null)
        {
            _ = router.PostEventAsync("theme.changed", new
            {
                preference = CurrentPreference,
                effectiveTheme = CurrentEffectiveTheme
            });
        }
    }

    private static bool IsValidPreference(string? preference)
    {
        return string.Equals(preference, "system", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(preference, "light", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(preference, "dark", StringComparison.OrdinalIgnoreCase);
    }
}
