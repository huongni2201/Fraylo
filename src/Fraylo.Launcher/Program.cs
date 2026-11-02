using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

// Bố cục cài đặt: <root>\FrayloLauncher.exe, <root>\current.json, <root>\versions\<ver>\Fraylo.exe
// Không tham số: mở phiên bản hiện hành. `--update --pid N --zip F --version V`: cài bản mới rồi mở lại.
var root = AppContext.BaseDirectory;
var versionsDir = Path.Combine(root, "versions");
var currentJson = Path.Combine(root, "current.json");
const string AppExe = "Fraylo.exe";

return args.Length > 0 && args[0] == "--update" ? Update(args) : Launch(args);

int Launch(string[] launchArgs)
{
    var version = ReadCurrentVersion();
    var exe = version is null ? null : Path.Combine(versionsDir, version, AppExe);
    // Cho phép mở lại phiên bản Fraylo cũ khi người dùng quay về bản trước.
    if (exe is not null && !File.Exists(exe))
        exe = Path.Combine(versionsDir, version!, "TiToAI.Desktop.exe");
    if (exe is null || !File.Exists(exe)) return 1;

    Process.Start(new ProcessStartInfo(exe, launchArgs) { WorkingDirectory = Path.GetDirectoryName(exe)! });
    return 0;
}

int Update(string[] updateArgs)
{
    var pid = int.Parse(Arg("--pid"));
    var zip = Arg("--zip");
    var version = Arg("--version");
    var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiToAI");
    var logFile = Path.Combine(dataDir, "logs", "updater.log");

    string Arg(string name) => updateArgs[Array.IndexOf(updateArgs, name) + 1];
    void Log(string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
    }

    try
    {
        Log($"Cập nhật lên {version}");
        try
        {
            // App còn chạy thì file DB/WAL chưa nhất quán: không sao lưu, không cài; mở lại bản cũ.
            if (!Process.GetProcessById(pid).WaitForExit(20_000))
                throw new TimeoutException("Fraylo chưa thoát sau 20 giây; hủy cập nhật.");
        }
        catch (ArgumentException) { }

        // Sao lưu database trước khi bản mới chạy migration; app đã thoát nên file không bị khóa.
        var backupDir = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(backupDir);
        foreach (var file in Directory.GetFiles(dataDir, "titoai.db*"))
            File.Copy(file, Path.Combine(backupDir, $"pre_update_{version}_{DateTime.Now:yyyyMMddHHmmss}_{Path.GetFileName(file)}"), true);

        var target = Path.Combine(versionsDir, version);
        var staging = target + ".tmp";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        if (!File.Exists(Path.Combine(staging, AppExe))) throw new FileNotFoundException($"Gói cập nhật thiếu {AppExe}.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(staging, target);

        var previous = ReadCurrentVersion();
        File.WriteAllText(currentJson, $"{{\"version\":\"{version}\",\"previous_version\":\"{previous}\"}}");

        // Giữ bản hiện hành + bản liền trước để có đường lùi thủ công.
        foreach (var dir in Directory.GetDirectories(versionsDir))
            if (Path.GetFileName(dir) != version && Path.GetFileName(dir) != previous)
                Directory.Delete(dir, true);

        Log("Hoàn tất");
    }
    catch (Exception ex)
    {
        Log($"LỖI: {ex}");
    }

    // Dù lỗi hay không vẫn mở lại app (current.json chỉ đổi khi cài thành công).
    return Launch([]);
}

string? ReadCurrentVersion()
{
    if (File.Exists(currentJson))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(currentJson));
            if (doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 } s) return s;
        }
        catch (JsonException) { }
    }
    if (!Directory.Exists(versionsDir)) return null;
    return Directory.GetDirectories(versionsDir).Select(Path.GetFileName)
        .OrderByDescending(n => Version.TryParse(n, out var v) ? v : new Version(0, 0)).FirstOrDefault();
}
