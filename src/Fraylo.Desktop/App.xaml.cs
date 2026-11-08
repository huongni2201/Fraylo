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
    private InstallationLock? _installationLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        CurrentRuntimePaths = DesktopRuntimePaths.FromCommandLineArgs(e.Args);
        var lockName = CurrentRuntimePaths.IsTestIsolation
            ? InstallationLock.Name + "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(CurrentRuntimePaths.DatabasePath.ToUpperInvariant())))
            : InstallationLock.Name;
        _installationLock = InstallationLock.TryAcquire(lockName);
        if (_installationLock is null)
        {
            MessageBox.Show("Fraylo đang mở hoặc đang cập nhật. Hãy dùng cửa sổ hiện tại hoặc chờ cập nhật hoàn tất.", "Fraylo");
            Shutdown();
            return;
        }
        if (!CurrentRuntimePaths.IsTestIsolation)
        {
            try { InstallationLock.EnsureNoOtherAppProcesses(); }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(ex.Message, "Fraylo");
                Shutdown();
                return;
            }
        }

        var mainWindow = new MainWindow(CurrentRuntimePaths);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _installationLock?.Dispose();
        base.OnExit(e);
    }
}
