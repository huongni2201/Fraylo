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
/// Kiểm tra bản mới trên Cloudflare R2 (manifest.json ở gốc bucket), tải gói zip (xác minh SHA-256) rồi giao cho FrayloLauncher.exe cài đặt.
/// Gói phát hành gồm Fraylo-{ver}-win-x64.zip và manifest.json (do scripts/package-release.ps1 tạo).
/// </summary>
public sealed class AppUpdateService(Action requestExit)
{
    // Gốc URL công khai của bucket Cloudflare R2 chứa manifest.json + zip, nhúng lúc build từ UPDATE_BASE_URL.
    private static readonly string UpdateBaseUrl =
        (Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateBaseUrl")?.Value ?? "").TrimEnd('/');
    // Khóa công khai ECDSA P-256 (SPKI, base64) của nhà phát hành; khóa riêng ký ở scripts/package-release.ps1.
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEzDap6qk89CfTAbawX5Z8pXCCHZYC9XiyovvFfBeIn8ov0nCa5r3bPAOB516IC0EmtQIrY6nzRalN1blY+tjFIw==";
    private static readonly HttpClient Http = CreateClient();

    public static string CurrentVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public async Task<UpdateInfo> CheckAsync(CancellationToken ct)
    {
        if (UpdateBaseUrl.Length == 0) throw new InvalidOperationException("Bản build này chưa cấu hình UPDATE_BASE_URL.");
        using var manifest = JsonDocument.Parse(await Http.GetStringAsync($"{UpdateBaseUrl}/manifest.json", ct));
        var m = manifest.RootElement;
        var latest = m.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
        var asset = m.GetProperty("asset");
        var sha256 = asset.GetProperty("sha256").GetString();
        var signature = m.TryGetProperty("signature", out var s) ? s.GetString() : null;
        var notes = m.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.Array
            ? n.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : [];

        // Manifest không ký đúng khóa của nhà phát hành thì coi như không có bản cập nhật.
        var signed = VerifyManifest(latest, sha256, signature, PublicKey);
        var zipUrl = signed ? $"{UpdateBaseUrl}/{Uri.EscapeDataString(asset.GetProperty("name").GetString() ?? "")}" : null;
        var hasUpdate = signed && Version.TryParse(latest, out var l)
            && Version.TryParse(CurrentVersion, out var c) && l > c;
        return new UpdateInfo(CurrentVersion, latest, hasUpdate, notes, zipUrl, signed ? sha256 : null);
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

    /// <summary>Kiểm chữ ký ECDSA P-256 trên "{version}|{sha256}"; mọi dữ liệu thiếu hoặc sai định dạng đều trả false.</summary>
    public static bool VerifyManifest(string? version, string? sha256, string? signature, string publicKey)
    {
        if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(sha256) || string.IsNullOrEmpty(signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            var data = System.Text.Encoding.UTF8.GetBytes($"{version}|{sha256.ToUpperInvariant()}");
            return key.VerifyData(data, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Fraylo-AutoUpdate/1.0");
        return client;
    }
}
