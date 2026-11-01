using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using Fraylo.Desktop.Bridge;

namespace Fraylo.Desktop.Hosting;

public interface IHostThemeService : IDisposable
{
    string CurrentPreference { get; }

    string CurrentEffectiveTheme { get; }

    event Action<string, string>? ThemeChanged;

    string GetEffectiveTheme(string preference);

    Task InitializePreferenceAsync(CancellationToken cancellationToken);

    void ApplyTheme(string effectiveTheme, Window? window, WebView2? webView);

    void SetPreference(string preference, Window? window, WebView2? webView);

    void RegisterBridgeHandlers(IBridgeDispatcher dispatcher, Func<BridgeMessageRouter?> getRouter);
}
