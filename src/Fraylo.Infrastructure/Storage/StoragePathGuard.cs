using System.IO;
using System.Security;
using System.Text.RegularExpressions;

namespace Fraylo.Infrastructure.Storage;

/// <summary>
/// Bảo vệ đường dẫn lưu trữ, chuẩn hóa đường dẫn, ngăn chặn Directory Traversal,
/// kiểm tra Symlink/Junction và thực hiện dry-run write probe theo SETTINGS_PLAN §2.
/// </summary>
public static class StoragePathGuard
{
    private static readonly char[] InvalidPathCharacters = Path.GetInvalidPathChars();
    private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Chuẩn hóa đường dẫn tuyệt đối: giải quyết relative segment, chuẩn hóa dấu phân cách,
    /// và loại bỏ trailing separator (trừ trường hợp drive root như C:\).
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Đường dẫn không được để trống.", nameof(path));
        }

        if (path.IndexOfAny(InvalidPathCharacters) >= 0)
        {
            throw new ArgumentException($"Đường dẫn chứa ký tự cấm: '{path}'.", nameof(path));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or PathTooLongException or ArgumentException)
        {
            throw new ArgumentException($"Đường dẫn không hợp lệ hoặc quá dài: '{path}'.", nameof(path), ex);
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Kiểm tra tính hợp lệ của đường dẫn tương đối (relative path) trong kho lưu trữ:
    /// - Không được null/trắng
    /// - Không được là rooted path hoặc chứa ký tự ổ đĩa ':'
    /// - Không chứa segment '..' (chặn directory traversal)
    /// - Không chứa ký tự không hợp lệ
    /// </summary>
    public static void EnsureValidRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Đường dẫn tương đối không được để trống.", nameof(relativePath));
        }

        if (relativePath.IndexOfAny(InvalidPathCharacters) >= 0 || relativePath.Contains(':'))
        {
            throw new ArgumentException($"Đường dẫn tương đối chứa ký tự không hợp lệ: '{relativePath}'.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new ArgumentException($"Đường dẫn tương đối không được bắt đầu bằng dấu phân cách gốc: '{relativePath}'.", nameof(relativePath));
        }

        var segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == ".." || segment == ".")
            {
                throw new ArgumentException($"Đường dẫn tương đối không được chứa segment chuyển hướng '{segment}': '{relativePath}'.", nameof(relativePath));
            }

            if (segment.IndexOfAny(InvalidFileNameCharacters) >= 0)
            {
                throw new ArgumentException($"Đường dẫn tương đối chứa phân đoạn có ký tự không hợp lệ '{segment}'.", nameof(relativePath));
            }
        }
    }

    /// <summary>
    /// Kiểm tra một đường dẫn candidate có thực sự là thư mục con (hoặc chính là) basePath hay không.
    /// Giải quyết cả symlink và junction points để đảm bảo đích thực tế không vượt ranh giới.
    /// </summary>
    public static bool IsSubPathOf(string basePath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string normalizedBase;
        string normalizedCandidate;

        try
        {
            normalizedBase = NormalizePath(basePath);
            normalizedCandidate = NormalizePath(candidatePath);
        }
        catch
        {
            return false;
        }

        if (!IsDirectPrefixOf(normalizedBase, normalizedCandidate))
        {
            return false;
        }

        // Resolve link targets (Junction / Symlink) qua từng cấp thư mục cha
        var realBase = ResolveRealPath(normalizedBase);
        var realCandidate = ResolveRealPath(normalizedCandidate);

        return IsDirectPrefixOf(realBase, realCandidate);
    }

    /// <summary>
    /// Ghép basePath với relativePath an toàn, đảm bảo đường dẫn kết quả nằm trọn vẹn trong basePath.
    /// </summary>
    public static string CombineAndVerify(string basePath, string relativePath)
    {
        EnsureValidRelativePath(relativePath);
        var normalizedBase = NormalizePath(basePath);
        var combined = Path.GetFullPath(Path.Combine(normalizedBase, relativePath));

        if (!IsSubPathOf(normalizedBase, combined))
        {
            throw new SecurityException($"Phát hiện vi phạm ranh giới lưu trữ: '{combined}' không thuộc root '{normalizedBase}'.");
        }

        return combined;
    }

    /// <summary>
    /// Đảm bảo thư mục tồn tại, nếu chưa có thì tạo mới.
    /// </summary>
    public static void EnsureDirectoryExists(string directoryPath)
    {
        var normalized = NormalizePath(directoryPath);
        if (!Directory.Exists(normalized))
        {
            Directory.CreateDirectory(normalized);
        }
    }

    /// <summary>
    /// Dry-run write probe: Thử tạo và xóa một tệp thăm dò .titoai_probe_{guid}.tmp 4 bytes trong thư mục
    /// để xác thực quyền ghi thực tế trên phân vùng đĩa.
    /// </summary>
    public static bool TryVerifyWritable(string directoryPath, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            errorMessage = "Đường dẫn thư mục không được để trống.";
            return false;
        }

        try
        {
            var normalized = NormalizePath(directoryPath);

            if (!Path.IsPathRooted(normalized))
            {
                errorMessage = "Đường dẫn thư mục phải là đường dẫn tuyệt đối.";
                return false;
            }

            if (!Directory.Exists(normalized))
            {
                Directory.CreateDirectory(normalized);
            }

            var probeFileName = $".titoai_probe_{Guid.NewGuid():N}.tmp";
            var probeFilePath = Path.Combine(normalized, probeFileName);

            // Ghi 4 bytes probe signature (ASCII "TITO")
            var probeData = new byte[] { 0x54, 0x49, 0x54, 0x4F };
            using (var fileStream = new FileStream(probeFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fileStream.Write(probeData, 0, probeData.Length);
                fileStream.Flush(flushToDisk: true);
            }

            if (File.Exists(probeFilePath))
            {
                File.Delete(probeFilePath);
            }

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            errorMessage = $"Không có quyền ghi vào thư mục: {ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            errorMessage = $"Lỗi I/O hoặc ổ đĩa không sẵn sàng: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"Đường dẫn thư mục không hợp lệ hoặc không thể ghi: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Thực hiện write probe, nếu thất bại sẽ ném ngoại lệ InvalidOperationException kèm thông báo chi tiết.
    /// </summary>
    public static void VerifyWritable(string directoryPath)
    {
        if (!TryVerifyWritable(directoryPath, out var errorMessage))
        {
            throw new InvalidOperationException(errorMessage ?? "Không thể ghi vào thư mục được chỉ định.");
        }
    }

    private static bool IsDirectPrefixOf(string parentPath, string childPath)
    {
        var parent = parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var child = childPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             childPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveRealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string normalized;
        try
        {
            normalized = NormalizePath(path);
        }
        catch
        {
            return path;
        }

        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
        {
            return normalized;
        }

        var relative = Path.GetRelativePath(root, normalized);
        if (relative == "." || string.IsNullOrWhiteSpace(relative))
        {
            return ResolveLinkOrCurrent(root);
        }

        string current = ResolveLinkOrCurrent(root);
        var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var next = Path.Combine(current, segment);
            current = ResolveLinkOrCurrent(next);
        }

        return NormalizePath(current);
    }

    private static string ResolveLinkOrCurrent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                return target != null ? NormalizePath(target.FullName) : path;
            }

            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                var target = di.ResolveLinkTarget(returnFinalTarget: true);
                return target != null ? NormalizePath(target.FullName) : path;
            }

            return path;
        }
        catch
        {
            return path;
        }
    }
}
