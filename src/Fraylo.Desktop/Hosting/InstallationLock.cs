using System;
using System.Diagnostics;
using System.Threading;

namespace Fraylo.Desktop.Hosting;

/// <summary>Khóa giữa các tiến trình app và updater, cùng tên AppMutex của bộ cài.</summary>
public sealed class InstallationLock : IDisposable
{
    public const string Name = "Fraylo.Desktop.Installation";
    private readonly Mutex _mutex;

    private InstallationLock(Mutex mutex) => _mutex = mutex;

    public static InstallationLock? TryAcquire(string name = Name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            try
            {
                if (mutex.WaitOne(0)) return new InstallationLock(mutex);
            }
            catch (AbandonedMutexException) { return new InstallationLock(mutex); }
            mutex.Dispose();
            return null;
        }
        catch { mutex.Dispose(); throw; }
    }

    public static void EnsureNoOtherAppProcesses()
    {
        // Bản cũ chưa có mutex cũng phải thoát trước khi backup/nâng schema.
        foreach (var process in Process.GetProcessesByName("Fraylo"))
        {
            using (process)
                if (process.Id != Environment.ProcessId && !process.HasExited)
                    throw new InvalidOperationException("Hãy đóng các cửa sổ Fraylo khác trước khi cập nhật.");
        }
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
