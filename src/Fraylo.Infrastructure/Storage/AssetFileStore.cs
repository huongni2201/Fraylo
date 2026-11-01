using System.IO;
using System.Security.Cryptography;

namespace Fraylo.Infrastructure.Storage;

public sealed record AssetFileResult(
    long ByteSize,
    string Sha256Hash
);

/// <summary>
/// Quản lý việc ghi tệp media/tài liệu vật lý vào Storage Root qua cơ chế Staging &amp; Atomic Finalize
/// theo docs/plans/implementation/T09b-media-assets.md và SETTINGS_PLAN §2–3.
/// </summary>
public sealed class AssetFileStore
{
    private const int BufferSize = 81920; // 80 KB chunk streaming

    /// <summary>
    /// Ghi dữ liệu từ Stream nguồn vào đường dẫn relativePath trong storageRootPath:
    /// - Streaming qua thư mục tạm .staging
    /// - Tính hash SHA-256 và byte size đồng thời
    /// - Atomic Move/Rename sang tệp đích
    /// - Dọn dẹp staging nếu phát sinh lỗi
    /// </summary>
    public async Task<AssetFileResult> SaveFileAsync(
        string storageRootPath,
        string relativePath,
        Stream sourceStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(sourceStream);

        var finalPath = StoragePathGuard.CombineAndVerify(storageRootPath, relativePath);
        var stagingDir = StoragePathGuard.CombineAndVerify(storageRootPath, ".staging");
        StoragePathGuard.EnsureDirectoryExists(stagingDir);

        var stagingFileName = $"{Guid.NewGuid():N}.tmp";
        var stagingFile = StoragePathGuard.CombineAndVerify(storageRootPath, Path.Combine(".staging", stagingFileName));

        var destDir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            if (!StoragePathGuard.IsSubPathOf(storageRootPath, destDir))
            {
                throw new System.Security.SecurityException($"Thư mục đích '{destDir}' nằm ngoài root '{storageRootPath}'.");
            }
            StoragePathGuard.EnsureDirectoryExists(destDir);
        }

        long totalBytes = 0;
        byte[] hashBytes;

        try
        {
            using (var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var fileStream = new FileStream(
                    stagingFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var buffer = new byte[BufferSize];
                    int bytesRead;

                    while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        incrementalHash.AppendData(buffer, 0, bytesRead);
                        totalBytes += bytesRead;
                    }

                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                hashBytes = incrementalHash.GetHashAndReset();
            }

            if (!string.IsNullOrEmpty(destDir))
            {
                StoragePathGuard.EnsureDirectoryExists(destDir);
            }

            // Atomic move/rename ghi đè tệp đích an toàn
            File.Move(stagingFile, finalPath, overwrite: true);

            var sha256Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            return new AssetFileResult(totalBytes, sha256Hex);
        }
        catch
        {
            SafeDeleteFile(stagingFile);
            throw;
        }
    }

    /// <summary>
    /// Ghi dữ liệu từ tệp cục bộ nguồn vào Storage Root qua cơ chế Staging &amp; Atomic Finalize.
    /// </summary>
    public async Task<AssetFileResult> SaveFileFromPathAsync(
        string storageRootPath,
        string relativePath,
        string sourceFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException($"Không tìm thấy tệp nguồn tại '{sourceFilePath}'.", sourceFilePath);
        }

        await using var fileStream = new FileStream(
            sourceFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await SaveFileAsync(storageRootPath, relativePath, fileStream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Xóa tệp đã chốt trong root khi metadata không ghi được. Lỗi xóa chỉ bỏ qua để không che lỗi gốc.</summary>
    public void DeleteStoredFile(string storageRootPath, string relativePath) =>
        SafeDeleteFile(StoragePathGuard.CombineAndVerify(storageRootPath, relativePath));

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Bỏ qua lỗi xóa tệp tạm để không làm giấu ngoại lệ gốc
        }
    }
}
