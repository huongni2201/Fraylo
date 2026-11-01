using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fraylo.Desktop.Hosting;

public sealed record UpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    bool HasUpdate,
    string[] ReleaseNotes,
    string? DownloadUrl,
    string? Sha256);

/// <summary>
/// Kiểm tra bản mới trên GitHub Releases, tải gói zip (xác minh SHA-256) rồi giao cho FrayloLauncher.exe cài đặt.
/// Gói phát hành gồm Fraylo-{ver}-win-x64.zip và manifest.json (do scripts/package-release.ps1 tạo).
/// </summary>
public sealed class AppUpdateService(Action requestExit)
{
    private const string Repo = "huongni2201/TitoAI";
    private static readonly HttpClient Http = CreateClient();

    public static string CurrentVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public async Task<UpdateInfo> CheckAsync(CancellationToken ct)
    {
        using var release = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        var root = release.RootElement;
        var latest = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        var notes = (root.GetProperty("body").GetString() ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimStart('-', '*', ' ')).Where(line => line.Length > 0).ToArray();

        string? zipUrl = null, manifestUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) zipUrl = url;
            else if (name == "manifest.json") manifestUrl = url;
        }

        string? sha256 = null;
        if (manifestUrl is not null)
        {
            using var manifest = JsonDocument.Parse(await Http.GetStringAsync(manifestUrl, ct));
            sha256 = manifest.RootElement.GetProperty("asset").GetProperty("sha256").GetString();
        }

        var hasUpdate = zipUrl is not null && sha256 is not null && Version.TryParse(latest, out var l)
            && Version.TryParse(CurrentVersion, out var c) && l > c;
        return new UpdateInfo(CurrentVersion, latest, hasUpdate, notes, zipUrl, sha256);
    }

    public async Task ApplyAsync(CancellationToken ct)
    {
        // Bản cài đặt chạy từ <root>\versions\<ver>\; bản dev thì không có launcher nên không tự cập nhật.
        var versionDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var launcher = versionDir.Parent?.Name == "versions"
            ? Path.Combine(versionDir.Parent.Parent!.FullName, "FrayloLauncher.exe")
            : null;
        // Bản được nâng cấp qua launcher Fraylo hiện hữu vẫn dùng launcher đó.
        if (launcher is not null && !File.Exists(launcher))
            launcher = Path.Combine(versionDir.Parent!.Parent!.FullName, "TiToAILauncher.exe");
        if (launcher is null || !File.Exists(launcher))
            throw new InvalidOperationException("Chỉ bản đã cài đặt bằng bộ cài mới tự cập nhật được.");

        var info = await CheckAsync(ct);
        if (!info.HasUpdate) throw new InvalidOperationException("Không có bản cập nhật mới.");

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiToAI", "downloads");
        Directory.CreateDirectory(downloads);
        var zip = Path.Combine(downloads, $"Fraylo-{info.LatestVersion}-win-x64.zip");
        await using (var source = await Http.GetStreamAsync(info.DownloadUrl, ct))
        await using (var target = File.Create(zip))
            await source.CopyToAsync(target, ct);

        string hash;
        await using (var file = File.OpenRead(zip))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidOperationException("Checksum SHA-256 của gói cập nhật không khớp.");
        }

        var start = new ProcessStartInfo(launcher) { UseShellExecute = false };
        foreach (var arg in new[] { "--update", "--pid", Environment.ProcessId.ToString(), "--zip", zip, "--version", info.LatestVersion })
            start.ArgumentList.Add(arg);
        Process.Start(start);
        requestExit();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Fraylo-AutoUpdate/1.0");
        return client;
    }
}
