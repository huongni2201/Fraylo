using System;
using System.Windows;
using Fraylo.Desktop.Hosting;

namespace Fraylo.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public static DesktopRuntimePaths? CurrentRuntimePaths { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        CurrentRuntimePaths = DesktopRuntimePaths.FromCommandLineArgs(e.Args);

        var mainWindow = new MainWindow(CurrentRuntimePaths);
        mainWindow.Show();
    }
}
