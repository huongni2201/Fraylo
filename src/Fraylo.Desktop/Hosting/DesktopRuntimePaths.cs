using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Desktop.Hosting;

/// <summary>
/// Quản lý và thẩm định đường dẫn runtime cho Desktop host (SQLite database và WebView2 profile).
/// Mặc định sản phẩm sử dụng %LOCALAPPDATA%/Fraylo.
/// Hỗ trợ cặp tham số dòng lệnh kiểm thử: --test-database-path và --test-webview2-profile-dir
/// để cô lập hoàn toàn môi trường test trong thư mục tạm mà không ghi đè dữ liệu production.
/// </summary>
public sealed class DesktopRuntimePaths
{
    public const string DatabasePathArg = "--test-database-path";
    public const string WebView2ProfileDirArg = "--test-webview2-profile-dir";

    public string DatabasePath { get; }
    public string WebView2ProfileDirectory { get; }
    public bool IsTestIsolation { get; }

    public DesktopRuntimePaths(string databasePath, string webView2ProfileDirectory, bool isTestIsolation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(webView2ProfileDirectory);

        DatabasePath = databasePath;
        WebView2ProfileDirectory = webView2ProfileDirectory;
        IsTestIsolation = isTestIsolation;
    }

    public static string GetDefaultDatabasePath()
    {
        return DatabasePathHelper.GetDefaultDatabasePath();
    }

    public static string GetDefaultWebView2ProfileDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, DatabasePathHelper.DefaultAppFolderName, "WebView2Profile");
    }

    public static string GetNormalizedTempDirectory()
    {
        string tempPath = Path.GetFullPath(Path.GetTempPath());
        if (!tempPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
            !tempPath.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            tempPath += Path.DirectorySeparatorChar;
        }
        return tempPath;
    }

    public static DesktopRuntimePaths CreateDefault()
    {
        return new DesktopRuntimePaths(
            GetDefaultDatabasePath(),
            GetDefaultWebView2ProfileDirectory(),
            isTestIsolation: false);
    }

    public static DesktopRuntimePaths CreateTest(string testDatabasePath, string testWebView2ProfileDirectory)
    {
        if (string.IsNullOrWhiteSpace(testDatabasePath))
        {
            throw new ArgumentException("Đường dẫn cơ sở dữ liệu kiểm thử không được để trống.", nameof(testDatabasePath));
        }

        if (string.IsNullOrWhiteSpace(testWebView2ProfileDirectory))
        {
            throw new ArgumentException("Đường dẫn thư mục profile WebView2 kiểm thử không được để trống.", nameof(testWebView2ProfileDirectory));
        }

        if (!Path.IsPathRooted(testDatabasePath) || !Path.IsPathFullyQualified(testDatabasePath))
        {
            throw new ArgumentException($"Đường dẫn cơ sở dữ liệu kiểm thử '{testDatabasePath}' phải là đường dẫn tuyệt đối.", nameof(testDatabasePath));
        }

        if (!Path.IsPathRooted(testWebView2ProfileDirectory) || !Path.IsPathFullyQualified(testWebView2ProfileDirectory))
        {
            throw new ArgumentException($"Đường dẫn thư mục profile WebView2 kiểm thử '{testWebView2ProfileDirectory}' phải là đường dẫn tuyệt đối.", nameof(testWebView2ProfileDirectory));
        }

        string normalizedDbPath = Path.GetFullPath(testDatabasePath);
        string normalizedProfileDir = Path.GetFullPath(testWebView2ProfileDirectory);

        string tempRoot = GetNormalizedTempDirectory();

        if (!IsSubpath(tempRoot, normalizedDbPath))
        {
            throw new ArgumentException($"Đường dẫn cơ sở dữ liệu kiểm thử '{normalizedDbPath}' phải nằm trong thư mục tạm hệ thống '{tempRoot}'.", nameof(testDatabasePath));
        }

        if (!IsSubpath(tempRoot, normalizedProfileDir))
        {
            throw new ArgumentException($"Đường dẫn thư mục profile WebView2 kiểm thử '{normalizedProfileDir}' phải nằm trong thư mục tạm hệ thống '{tempRoot}'.", nameof(testWebView2ProfileDirectory));
        }

        string defaultDbPath = Path.GetFullPath(GetDefaultDatabasePath());
        string defaultProfileDir = Path.GetFullPath(GetDefaultWebView2ProfileDirectory());

        if (string.Equals(normalizedDbPath, defaultDbPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedDbPath, defaultProfileDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Đường dẫn cơ sở dữ liệu kiểm thử '{normalizedDbPath}' không được trùng với vị trí production mặc định.", nameof(testDatabasePath));
        }

        if (string.Equals(normalizedProfileDir, defaultDbPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedProfileDir, defaultProfileDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Đường dẫn thư mục profile WebView2 kiểm thử '{normalizedProfileDir}' không được trùng với vị trí production mặc định.", nameof(testWebView2ProfileDirectory));
        }

        if (string.Equals(normalizedDbPath, normalizedProfileDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Đường dẫn cơ sở dữ liệu và thư mục profile WebView2 kiểm thử phải tách biệt nhau.");
        }

        return new DesktopRuntimePaths(normalizedDbPath, normalizedProfileDir, isTestIsolation: true);
    }

    public static DesktopRuntimePaths FromCommandLineArgs(IEnumerable<string>? args = null)
    {
        IReadOnlyList<string> tokens;
        if (args != null)
        {
            tokens = args.ToList();
        }
        else
        {
            var raw = Environment.GetCommandLineArgs();
            tokens = raw.Length > 1 ? raw.Skip(1).ToList() : Array.Empty<string>();
        }

        string? testDb = null;
        string? testProfile = null;

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (token.Equals(DatabasePathArg, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < tokens.Count)
                {
                    testDb = tokens[++i];
                }
                else
                {
                    throw new ArgumentException($"Tham số '{DatabasePathArg}' yêu cầu giá trị đường dẫn.");
                }
            }
            else if (token.StartsWith(DatabasePathArg + "=", StringComparison.OrdinalIgnoreCase))
            {
                testDb = token.Substring(DatabasePathArg.Length + 1);
            }
            else if (token.Equals(WebView2ProfileDirArg, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < tokens.Count)
                {
                    testProfile = tokens[++i];
                }
                else
                {
                    throw new ArgumentException($"Tham số '{WebView2ProfileDirArg}' yêu cầu giá trị đường dẫn.");
                }
            }
            else if (token.StartsWith(WebView2ProfileDirArg + "=", StringComparison.OrdinalIgnoreCase))
            {
                testProfile = token.Substring(WebView2ProfileDirArg.Length + 1);
            }
        }

        if (testDb == null && testProfile == null)
        {
            return CreateDefault();
        }

        if (testDb == null || testProfile == null)
        {
            throw new ArgumentException(
                $"Cả hai tham số kiểm thử '{DatabasePathArg}' và '{WebView2ProfileDirArg}' bắt buộc phải được cung cấp đồng thời theo cặp.");
        }

        testDb = testDb.Trim('\"');
        testProfile = testProfile.Trim('\"');

        return CreateTest(testDb, testProfile);
    }

    private static bool IsSubpath(string baseDirWithSeparator, string candidatePath)
    {
        if (!candidatePath.StartsWith(baseDirWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return candidatePath.Length > baseDirWithSeparator.Length;
    }
}
