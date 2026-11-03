using System;
using System.IO;
using Fraylo.Desktop.Hosting;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>
/// Smoke checks cho bộ định tuyến và thẩm định đường dẫn runtime Desktop (T05-ISO):
/// Xác minh hành vi production mặc định, đường dẫn test tạm hợp lệ, và các điều kiện từ chối nghiêm ngặt
/// (thiếu cặp, tương đối, ngoài temp, trùng production mặc định, đường dẫn trùng nhau).
/// </summary>
public static class DesktopRuntimePathsSmokeTests
{
    public static void Test_DefaultPaths_UsesLocalAppDataAndProductionDefaults()
    {
        // 1. Kiểm tra CreateDefault()
        var defaultPaths = DesktopRuntimePaths.CreateDefault();
        Assert(!defaultPaths.IsTestIsolation, "Mặc định không được đánh dấu là test isolation");
        Assert(string.Equals(defaultPaths.DatabasePath, DatabasePathHelper.GetDefaultDatabasePath(), StringComparison.OrdinalIgnoreCase),
            "DatabasePath mặc định phải khớp với DatabasePathHelper.GetDefaultDatabasePath()");
        Assert(string.Equals(defaultPaths.WebView2ProfileDirectory, DesktopRuntimePaths.GetDefaultWebView2ProfileDirectory(), StringComparison.OrdinalIgnoreCase),
            "WebView2ProfileDirectory mặc định phải khớp với DesktopRuntimePaths.GetDefaultWebView2ProfileDirectory()");

        // 2. Kiểm tra FromCommandLineArgs khi không có tham số test
        var emptyArgsPaths = DesktopRuntimePaths.FromCommandLineArgs(Array.Empty<string>());
        Assert(!emptyArgsPaths.IsTestIsolation, "Tham số rỗng phải trả về cấu hình mặc định");
        Assert(string.Equals(emptyArgsPaths.DatabasePath, defaultPaths.DatabasePath, StringComparison.OrdinalIgnoreCase),
            "DatabasePath khi tham số rỗng phải là mặc định production");
        Assert(string.Equals(emptyArgsPaths.WebView2ProfileDirectory, defaultPaths.WebView2ProfileDirectory, StringComparison.OrdinalIgnoreCase),
            "WebView2ProfileDirectory khi tham số rỗng phải là mặc định production");

        // 3. Kiểm tra FromCommandLineArgs khi chỉ có các cờ không liên quan
        var unrelatedArgsPaths = DesktopRuntimePaths.FromCommandLineArgs(new[] { "--foo", "bar", "--verbose" });
        Assert(!unrelatedArgsPaths.IsTestIsolation, "Tham số không liên quan phải giữ mặc định production");
        Assert(string.Equals(unrelatedArgsPaths.DatabasePath, defaultPaths.DatabasePath, StringComparison.OrdinalIgnoreCase),
            "DatabasePath không đổi khi chỉ có cờ không liên quan");

        // 4. Xác nhận đường dẫn chứa thư mục chuẩn Fraylo
        Assert(defaultPaths.DatabasePath.Contains("Fraylo", StringComparison.OrdinalIgnoreCase),
            "DatabasePath mặc định phải nằm dưới thư mục Fraylo");
        Assert(defaultPaths.WebView2ProfileDirectory.Contains("Fraylo", StringComparison.OrdinalIgnoreCase),
            "WebView2ProfileDirectory mặc định phải nằm dưới thư mục Fraylo");
    }

    public static void Test_ValidTestPair_NormalizedAndAccepted()
    {
        string tempRoot = DesktopRuntimePaths.GetNormalizedTempDirectory();
        string testFolder = Path.Combine(tempRoot, "Fraylo_IsoTest_" + Guid.NewGuid().ToString("N"));
        string testDb = Path.Combine(testFolder, "subfolder", "isolated.db");
        string testProfile = Path.Combine(testFolder, "wv2_profile");

        // 1. Cú pháp truyền cờ cách nhau bởi khoảng trắng
        var paths = DesktopRuntimePaths.FromCommandLineArgs(new[]
        {
            DesktopRuntimePaths.DatabasePathArg, testDb,
            DesktopRuntimePaths.WebView2ProfileDirArg, testProfile
        });

        Assert(paths.IsTestIsolation, "Cặp tham số test hợp lệ phải bật IsTestIsolation = true");
        Assert(string.Equals(paths.DatabasePath, Path.GetFullPath(testDb), StringComparison.OrdinalIgnoreCase),
            "DatabasePath phải được chuẩn hóa đúng đường dẫn test");
        Assert(string.Equals(paths.WebView2ProfileDirectory, Path.GetFullPath(testProfile), StringComparison.OrdinalIgnoreCase),
            "WebView2ProfileDirectory phải được chuẩn hóa đúng đường dẫn test");

        // 2. Cú pháp dùng dấu bằng: --arg=value
        var equalPaths = DesktopRuntimePaths.FromCommandLineArgs(new[]
        {
            $"{DesktopRuntimePaths.DatabasePathArg}={testDb}",
            $"{DesktopRuntimePaths.WebView2ProfileDirArg}={testProfile}"
        });

        Assert(equalPaths.IsTestIsolation, "Cú pháp --arg=value phải bật IsTestIsolation");
        Assert(string.Equals(equalPaths.DatabasePath, Path.GetFullPath(testDb), StringComparison.OrdinalIgnoreCase),
            "DatabasePath với cú pháp dấu bằng phải khớp");
        Assert(string.Equals(equalPaths.WebView2ProfileDirectory, Path.GetFullPath(testProfile), StringComparison.OrdinalIgnoreCase),
            "WebView2ProfileDirectory với cú pháp dấu bằng phải khớp");

        // 3. Đảo thứ tự các cờ
        var reversedPaths = DesktopRuntimePaths.FromCommandLineArgs(new[]
        {
            DesktopRuntimePaths.WebView2ProfileDirArg, testProfile,
            DesktopRuntimePaths.DatabasePathArg, testDb
        });

        Assert(reversedPaths.IsTestIsolation, "Đảo thứ tự cờ phải hợp lệ");
        Assert(string.Equals(reversedPaths.DatabasePath, Path.GetFullPath(testDb), StringComparison.OrdinalIgnoreCase),
            "DatabasePath khi đảo thứ tự cờ phải khớp");
        Assert(string.Equals(reversedPaths.WebView2ProfileDirectory, Path.GetFullPath(testProfile), StringComparison.OrdinalIgnoreCase),
            "WebView2ProfileDirectory khi đảo thứ tự cờ phải khớp");
    }

    public static void Test_MissingPair_RejectedWithArgumentException()
    {
        string tempRoot = DesktopRuntimePaths.GetNormalizedTempDirectory();
        string validTempDb = Path.Combine(tempRoot, "test.db");
        string validTempProfile = Path.Combine(tempRoot, "wv2");

        // Chỉ có --test-database-path
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, validTempDb
            });
        }, "Chỉ truyền --test-database-path mà thiếu --test-webview2-profile-dir");

        // Chỉ có --test-webview2-profile-dir
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.WebView2ProfileDirArg, validTempProfile
            });
        }, "Chỉ truyền --test-webview2-profile-dir mà thiếu --test-database-path");

        // Cờ không có giá trị ở cuối
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg
            });
        }, "Cờ --test-database-path thiếu giá trị");
    }

    public static void Test_RelativePath_RejectedWithArgumentException()
    {
        string tempRoot = DesktopRuntimePaths.GetNormalizedTempDirectory();
        string validTempProfile = Path.Combine(tempRoot, "wv2");
        string validTempDb = Path.Combine(tempRoot, "test.db");

        // 1. Tên file tương đối đơn giản
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, "relative.db",
                DesktopRuntimePaths.WebView2ProfileDirArg, validTempProfile
            });
        }, "DatabasePath là đường dẫn tương đối đơn");

        // 2. Thư mục profile tương đối
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, validTempDb,
                DesktopRuntimePaths.WebView2ProfileDirArg, "relative_profile"
            });
        }, "ProfileDir là đường dẫn tương đối đơn");

        // 3. Đường dẫn bắt đầu bằng .\\
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, @".\sub\test.db",
                DesktopRuntimePaths.WebView2ProfileDirArg, validTempProfile
            });
        }, "DatabasePath bắt đầu bằng .\\");
    }

    public static void Test_OutsideTemp_RejectedWithArgumentException()
    {
        string tempRoot = DesktopRuntimePaths.GetNormalizedTempDirectory();
        string validTempDb = Path.Combine(tempRoot, "test.db");
        string validTempProfile = Path.Combine(tempRoot, "wv2");

        string userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string outsideDb = Path.GetFullPath(Path.Combine(userFolder, "outside.db"));
        string outsideProfile = Path.GetFullPath(Path.Combine(userFolder, "outside_profile"));

        // 1. Database ngoài temp
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, outsideDb,
                DesktopRuntimePaths.WebView2ProfileDirArg, validTempProfile
            });
        }, "DatabasePath nằm ngoài thư mục temp");

        // 2. Profile ngoài temp
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, validTempDb,
                DesktopRuntimePaths.WebView2ProfileDirArg, outsideProfile
            });
        }, "ProfileDir nằm ngoài thư mục temp");

        // 3. Directory traversal để thoát khỏi thư mục temp
        string traversalPath = Path.Combine(tempRoot, "..", "outside_traversal.db");
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, traversalPath,
                DesktopRuntimePaths.WebView2ProfileDirArg, validTempProfile
            });
        }, "DatabasePath dùng traversal để thoát khỏi temp");
    }

    public static void Test_ProductionCollision_RejectedWithArgumentException()
    {
        string tempRoot = DesktopRuntimePaths.GetNormalizedTempDirectory();
        string validTempProfile = Path.Combine(tempRoot, "wv2");
        string validTempDb = Path.Combine(tempRoot, "test.db");

        string prodDb = DatabasePathHelper.GetDefaultDatabasePath();
        string prodProfile = DesktopRuntimePaths.GetDefaultWebView2ProfileDirectory();

        // 1. Database trỏ vào file production mặc định
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, prodDb,
                DesktopRuntimePaths.WebView2ProfileDirArg, validTempProfile
            });
        }, "DatabasePath trùng file DB production mặc định");

        // 2. Profile trỏ vào thư mục profile production mặc định
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, validTempDb,
                DesktopRuntimePaths.WebView2ProfileDirArg, prodProfile
            });
        }, "ProfileDir trùng thư mục profile production mặc định");

        // 3. Chéo: Database trỏ vào profile production
        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, prodProfile,
                DesktopRuntimePaths.WebView2ProfileDirArg, validTempProfile
            });
        }, "DatabasePath trỏ vào thư mục profile production");
    }

    public static void Test_IdenticalPaths_RejectedWithArgumentException()
    {
        string tempRoot = DesktopRuntimePaths.GetNormalizedTempDirectory();
        string samePath = Path.Combine(tempRoot, "identical_path");

        AssertThrowsArgumentException(() =>
        {
            DesktopRuntimePaths.FromCommandLineArgs(new[]
            {
                DesktopRuntimePaths.DatabasePathArg, samePath,
                DesktopRuntimePaths.WebView2ProfileDirArg, samePath
            });
        }, "DatabasePath và WebView2ProfileDirectory trỏ vào cùng một đường dẫn");
    }

    private static void AssertThrowsArgumentException(Action action, string scenarioName)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Kịch bản '{scenarioName}' lẽ ra phải ném ArgumentException nhưng không ném.");
        }
        catch (ArgumentException)
        {
            // Bắt đúng exception mong đợi
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
