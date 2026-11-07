using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Projects;
using Fraylo.Application.Scenes;
using Fraylo.Contracts.Assets;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Application.Assets;

public class AssetValidationException : Exception
{
    public AssetValidationException(string message) : base(message) { }
    public AssetValidationException(string message, Exception innerException) : base(message, innerException) { }
}

public class AssetNotFoundException : Exception
{
    public AssetNotFoundException(string assetId)
        : base($"Không tìm thấy tệp tài nguyên (Asset) với mã '{assetId}'.") { }
}

/// <summary>
/// Dịch vụ quản lý tài nguyên media (Audio MP3/WAV, Ảnh tham chiếu, Video)
/// theo docs/plans/implementation/T09b-media-assets.md và SETTINGS_PLAN.md §2–3.
/// </summary>
/// <summary>Kết quả đối soát tệp: số tệp staging đã dọn và tệp trong root không có metadata (đường dẫn tương đối).</summary>
public sealed record AssetFileReconcileReport(int StagingFilesRemoved, IReadOnlyList<string> OrphanRelativePaths);

public sealed class AssetService
{
    private static readonly HashSet<string> AllowedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".wav"
    };

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp"
    };

    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly StorageRootRegistry _rootRegistry;
    private readonly AssetFileStore _fileStore;

    public AssetService(
        DbContextOptions<FrayloDbContext> dbOptions,
        StorageRootRegistry rootRegistry,
        AssetFileStore fileStore)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _rootRegistry = rootRegistry ?? throw new ArgumentNullException(nameof(rootRegistry));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
    }

    /// <summary>
    /// Nhập tệp âm thanh (MP3 hoặc WAV) vào thư mục Audio cố định (root-internal-audio),
    /// tính toán SHA-256 và kích thước tệp, lưu Asset và liên kết với Scene.
    /// </summary>
    public async Task<ImportAudioResponse> ImportAudioAsync(
        ImportAudioRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectId = NormalizeGuid(request.ProjectId, nameof(request.ProjectId));
        var sceneId = NormalizeGuid(request.SceneId, nameof(request.SceneId));

        if (string.IsNullOrWhiteSpace(request.SourceFilePath) || !File.Exists(request.SourceFilePath))
        {
            throw new AssetValidationException($"Tệp âm thanh nguồn không tồn tại: '{request.SourceFilePath}'.");
        }

        var ext = Path.GetExtension(request.SourceFilePath).ToLowerInvariant();
        if (!AllowedAudioExtensions.Contains(ext))
        {
            throw new AssetValidationException($"Chỉ hỗ trợ tệp âm thanh định dạng .mp3 hoặc .wav. Định dạng hiện tại: '{ext}'.");
        }

        // Kiểm tra định dạng nhị phân thực tế và kích thước tệp
        ValidateAudioFile(request.SourceFilePath, ext);

        await using var dbContext = new FrayloDbContext(_dbOptions);

        // Kiểm tra quyền liên kết Project và Scene.
        await VerifySceneOwnershipAsync(dbContext, projectId, sceneId, cancellationToken).ConfigureAwait(false);

        var audioRootId = StorageRootRegistry.RootInternalAudioId;
        var audioRootPath = await _rootRegistry.ResolveRootPathAsync(audioRootId, cancellationToken).ConfigureAwait(false);

        var assetId = Guid.NewGuid().ToString("N");
        var relativePath = $"{projectId}/audio/{assetId}{ext}";

        // Ghi tệp vật lý qua staging & atomic move
        var saveResult = await _fileStore.SaveFileFromPathAsync(
            audioRootPath,
            relativePath,
            request.SourceFilePath,
            cancellationToken
        ).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToString("O");
        var mimeType = ext == ".mp3" ? "audio/mpeg" : "audio/wav";

        var asset = new Asset
        {
            Id = assetId,
            ProjectId = projectId,
            AssetType = "Audio",
            StorageRootId = audioRootId,
            RelativePath = relativePath,
            ByteSize = saveResult.ByteSize,
            Sha256Hash = saveResult.Sha256Hash,
            Status = "Linked",
            MimeType = mimeType,
            CreatedAtUtc = now
        };

        dbContext.Assets.Add(asset);

        // Cập nhật Scene.AudioAssetId
        var scene = await dbContext.Scenes.FirstAsync(s => s.Id == sceneId, cancellationToken).ConfigureAwait(false);
        scene.AudioAssetId = assetId;
        scene.UpdatedAtUtc = now;

        await SaveAssetMetadataAsync(dbContext, audioRootPath, relativePath, cancellationToken).ConfigureAwait(false);

        return new ImportAudioResponse(ToDto(asset));
    }

    /// <summary>
    /// Nhập ảnh tham chiếu (Reference Image) vào thư mục Ảnh AI hiện hành,
    /// lưu Asset và liên kết với Scene.ImageAssetId.
    /// </summary>
    public async Task<SelectReferenceImageResponse> SelectReferenceImageAsync(
        SelectReferenceImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectId = NormalizeGuid(request.ProjectId, nameof(request.ProjectId));
        var sceneId = NormalizeGuid(request.SceneId, nameof(request.SceneId));

        if (string.IsNullOrWhiteSpace(request.SourceFilePath) || !File.Exists(request.SourceFilePath))
        {
            throw new AssetValidationException($"Tệp ảnh nguồn không tồn tại: '{request.SourceFilePath}'.");
        }

        var ext = Path.GetExtension(request.SourceFilePath).ToLowerInvariant();
        if (!AllowedImageExtensions.Contains(ext))
        {
            throw new AssetValidationException($"Chỉ hỗ trợ tệp ảnh định dạng .png, .jpg, .jpeg, .webp. Định dạng hiện tại: '{ext}'.");
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        await VerifySceneOwnershipAsync(dbContext, projectId, sceneId, cancellationToken).ConfigureAwait(false);

        var currentRoots = await _rootRegistry.GetCurrentRootsAsync(cancellationToken).ConfigureAwait(false);
        var imagesRootId = currentRoots.ImagesRootId;
        var imagesRootPath = currentRoots.ImagesPath;

        var assetId = Guid.NewGuid().ToString("N");
        var relativePath = $"{projectId}/images/{assetId}{ext}";

        var saveResult = await _fileStore.SaveFileFromPathAsync(
            imagesRootPath,
            relativePath,
            request.SourceFilePath,
            cancellationToken
        ).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToString("O");
        var mimeType = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        var asset = new Asset
        {
            Id = assetId,
            ProjectId = projectId,
            AssetType = "Image",
            StorageRootId = imagesRootId,
            RelativePath = relativePath,
            ByteSize = saveResult.ByteSize,
            Sha256Hash = saveResult.Sha256Hash,
            Status = "Linked",
            MimeType = mimeType,
            CreatedAtUtc = now
        };

        dbContext.Assets.Add(asset);

        var scene = await dbContext.Scenes.FirstAsync(s => s.Id == sceneId, cancellationToken).ConfigureAwait(false);
        scene.ImageAssetId = assetId;
        scene.UpdatedAtUtc = now;

        await SaveAssetMetadataAsync(dbContext, imagesRootPath, relativePath, cancellationToken).ConfigureAwait(false);

        return new SelectReferenceImageResponse(ToDto(asset));
    }

    /// <summary>
    /// Tải lên tài nguyên media (Ảnh hoặc Audio) vào thư viện media cấp ứng dụng.
    /// lưu tệp vật lý vào Storage Root qua cơ chế staging & atomic move,
    /// đồng thời lưu bản ghi Asset vào cơ sở dữ liệu để có thể tái sử dụng toàn ứng dụng.
    /// Hỗ trợ cả Base64Data và SourceFilePath.
    /// </summary>
    /// <summary>Lưu MP4 do provider trả về thành Asset kiểu Video; chỉ nhận tệp có track video và thời lượng hợp lệ.</summary>
    public async Task<AssetDto> SaveVideoAssetAsync(string projectId, string sourcePath, string? storageRootId = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath) || !string.Equals(Path.GetExtension(sourcePath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new AssetValidationException("Video đầu ra phải là tệp .mp4 tồn tại.");

        VerifyPlayableMp4(sourcePath);

        var roots = await _rootRegistry.GetCurrentRootsAsync(cancellationToken).ConfigureAwait(false);
        var videoRootId = string.IsNullOrWhiteSpace(storageRootId) ? roots.VideosRootId : storageRootId;
        var videoRootPath = string.IsNullOrWhiteSpace(storageRootId)
            ? roots.VideosPath
            : await _rootRegistry.ResolveRootPathAsync(storageRootId, cancellationToken).ConfigureAwait(false);
        var assetId = Guid.NewGuid().ToString("N");
        var relativePath = $"{projectId}/videos/{assetId}.mp4";
        await using var stream = File.OpenRead(sourcePath);
        var saved = await _fileStore.SaveFileAsync(videoRootPath, relativePath, stream, cancellationToken).ConfigureAwait(false);

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var asset = new Asset
        {
            Id = assetId,
            ProjectId = projectId,
            AssetType = "Video",
            StorageRootId = videoRootId,
            RelativePath = relativePath,
            ByteSize = saved.ByteSize,
            Sha256Hash = saved.Sha256Hash,
            Status = "Linked",
            MimeType = "video/mp4",
            CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        dbContext.Assets.Add(asset);
        await SaveAssetMetadataAsync(dbContext, videoRootPath, relativePath, cancellationToken).ConfigureAwait(false);
        return ToDto(asset);
    }

    /// <summary>
    /// Đọc cấu trúc hộp MP4: phải có ftyp, mdat và moov với mvhd có thời lượng dương cùng một track 'vide'.
    /// Không dùng FFmpeg; chỉ đọc header hộp và phần moov (nhỏ) vào bộ nhớ.
    /// </summary>
    internal static void VerifyPlayableMp4(string path)
    {
        using var stream = File.OpenRead(path);
        byte[]? moov = null;
        var hasMdat = false;
        long position = 0;
        while (position < stream.Length)
        {
            var (type, headerSize, size) = ReadMp4BoxHeader(stream, position);
            if (position == 0 && type != "ftyp") throw InvalidMp4();
            if (type == "mdat") hasMdat = true;
            if (type == "moov")
            {
                if (size - headerSize > 4 * 1024 * 1024) throw InvalidMp4();
                moov = new byte[size - headerSize];
                stream.Position = position + headerSize;
                stream.ReadExactly(moov);
            }
            position += size;
        }

        if (moov is null || !hasMdat || !HasPlayableVideoTrack(moov)) throw InvalidMp4();
    }

    internal static (string Type, int HeaderSize, long Size) ReadMp4BoxHeader(Stream stream, long position)
    {
        Span<byte> header = stackalloc byte[16];
        stream.Position = position;
        if (stream.ReadAtLeast(header[..8], 8, throwOnEndOfStream: false) < 8) throw InvalidMp4();
        var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = Encoding.ASCII.GetString(header.Slice(4, 4));
        var headerSize = 8;
        if (size == 1)
        {
            if (stream.ReadAtLeast(header[8..16], 8, throwOnEndOfStream: false) < 8) throw InvalidMp4();
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = stream.Length - position;
        }
        if (size < headerSize || size < 0 || size > stream.Length - position) throw InvalidMp4();
        return (type, headerSize, size);
    }

    private static bool HasPlayableVideoTrack(byte[] moov)
    {
        var mvhd = Mp4Children(moov, 0, moov.Length).FirstOrDefault(c => c.Type == "mvhd");
        if (mvhd.Type is null || mvhd.End - mvhd.Start < 20) return false;
        var version = moov[mvhd.Start];
        if (version > 1 || (version == 1 && mvhd.End - mvhd.Start < 32)) return false;
        var timescale = version == 1 ? BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mvhd.Start + 20)) : BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mvhd.Start + 12));
        var duration = version == 1 ? BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(mvhd.Start + 24)) : BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mvhd.Start + 16));
        if (timescale == 0 || duration == 0) return false;

        foreach (var trak in Mp4Children(moov, 0, moov.Length).Where(c => c.Type == "trak"))
        {
            var mdia = Mp4Children(moov, trak.Start, trak.End).FirstOrDefault(c => c.Type == "mdia");
            var hdlr = Mp4Children(moov, mdia.Start, mdia.End).FirstOrDefault(c => c.Type == "hdlr");
            if (hdlr.Type is not null && hdlr.End - hdlr.Start >= 12 &&
                moov.AsSpan(hdlr.Start + 8, 4).SequenceEqual("vide"u8))
            {
                return true;
            }
        }
        return false;
    }

    internal static List<(string Type, int Start, int End)> Mp4Children(byte[] data, int start, int end)
    {
        var children = new List<(string Type, int Start, int End)>();
        var position = start;
        while (position < end)
        {
            if (end - position < 8) throw InvalidMp4();
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            var type = Encoding.ASCII.GetString(data, position + 4, 4);
            var headerSize = 8;
            if (size == 1)
            {
                if (end - position < 16) throw InvalidMp4();
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }
            if (size < headerSize || size > end - position) throw InvalidMp4();
            children.Add((type, position + headerSize, position + (int)size));
            position += (int)size;
        }
        return children;
    }

    private static AssetValidationException InvalidMp4() =>
        new("Tệp video tải về không phải MP4 hợp lệ hoặc không có track hình ảnh với thời lượng.");

    /// <summary>
    /// Đối soát sau crash: xóa tệp staging bị bỏ dở quá 1 ngày và liệt kê tệp trong root không có metadata Asset.
    /// Không tự xóa tệp đã chốt: DB có thể bị reset trong khi tệp vẫn còn.
    /// </summary>
    public async Task<AssetFileReconcileReport> ReconcileFilesAsync(CancellationToken cancellationToken = default)
    {
        var knownByRoot = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var dbContext = new FrayloDbContext(_dbOptions))
        {
            var rows = await dbContext.Assets.AsNoTracking()
                .Select(a => new { a.StorageRootId, a.RelativePath })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (!knownByRoot.TryGetValue(row.StorageRootId, out var known))
                {
                    knownByRoot[row.StorageRootId] = known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                known.Add(row.RelativePath.Replace('\\', '/'));
            }
        }

        var stagingRemoved = 0;
        var orphans = new List<string>();
        var staleBefore = DateTime.UtcNow.AddDays(-1);
        // Duyệt mọi root đã đăng ký, kể cả root chưa có Asset (crash lần import đầu hoặc DB bị reset).
        var rootIds = (await _rootRegistry.GetRegisteredRootIdsAsync(cancellationToken).ConfigureAwait(false))
            .Union(knownByRoot.Keys, StringComparer.Ordinal);
        foreach (var rootId in rootIds)
        {
            string rootPath;
            try
            {
                rootPath = await _rootRegistry.ResolveRootPathAsync(rootId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // Root không còn đăng ký: bỏ qua, không đoán đường dẫn.
            }
            if (!Directory.Exists(rootPath)) continue;

            var known = knownByRoot.TryGetValue(rootId, out var rootKnown)
                ? rootKnown
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var stagingDir = Path.Combine(rootPath, ".staging");
            // Junction/symlink .staging có thể trỏ ra ngoài root: không dọn qua liên kết.
            if (Directory.Exists(stagingDir) && !IsReparsePoint(stagingDir))
            {
                foreach (var file in Directory.EnumerateFiles(stagingDir))
                {
                    if (File.GetLastWriteTimeUtc(file) > staleBefore) continue;
                    try
                    {
                        File.Delete(file);
                        stagingRemoved++;
                    }
                    catch (IOException)
                    {
                        // Tệp đang bị khóa: lần đối soát sau sẽ thử lại.
                    }
                }
            }

            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                if (relative.StartsWith(".staging/", StringComparison.Ordinal)) continue;
                if (!known.Contains(relative)) orphans.Add(relative);
            }
        }

        return new AssetFileReconcileReport(stagingRemoved, orphans);
    }

    private static bool IsReparsePoint(string path) =>
        (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Ghi metadata Asset; nếu commit lỗi thì xóa tệp vừa chuyển vào root để không thành tệp mồ côi.</summary>
    private async Task SaveAssetMetadataAsync(FrayloDbContext dbContext, string storageRootPath, string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _fileStore.DeleteStoredFile(storageRootPath, relativePath);
            throw;
        }
    }

    public async Task<UploadAssetResponse> UploadAssetAsync(
        UploadAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? projectId = null;
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            projectId = NormalizeGuid(request.ProjectId, nameof(request.ProjectId));
        }

        var assetType = string.IsNullOrWhiteSpace(request.AssetType) ? "Image" : request.AssetType.Trim();
        bool isAudio = assetType.Equals("Audio", StringComparison.OrdinalIgnoreCase);
        bool isImage = assetType.Equals("Image", StringComparison.OrdinalIgnoreCase);
        bool isVideo = assetType.Equals("Video", StringComparison.OrdinalIgnoreCase);

        if (!isAudio && !isImage && !isVideo)
        {
            throw new AssetValidationException($"Chỉ hỗ trợ loại tài nguyên Image, Audio hoặc Video. Loại nhận được: '{request.AssetType}'.");
        }
        if (isVideo && (!string.IsNullOrWhiteSpace(request.Base64Data) || request.Multiple))
            throw new AssetValidationException("Video mẫu phải được chọn một tệp qua hộp thoại, không gửi Base64 hoặc nhiều video.");

        await using var dbContext = new FrayloDbContext(_dbOptions);

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var projectExists = await dbContext.Projects
                .AsNoTracking()
                .AnyAsync(p => p.Id == projectId, cancellationToken)
                .ConfigureAwait(false);

            if (!projectExists)
            {
                // Không lưu thành asset toàn app: người dùng sẽ không thấy tệp trong Project đã chọn.
                throw new AssetValidationException($"Không tìm thấy Project '{projectId}'. Tệp chưa được tải lên.");
            }
        }

        string extension;
        string storageRootId;
        string storageRootPath;

        if (isAudio)
        {
            storageRootId = StorageRootRegistry.RootInternalAudioId;
            storageRootPath = await _rootRegistry.ResolveRootPathAsync(storageRootId, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(request.StorageRootId))
        {
            // Output của job lưu vào đúng root đã chốt khi tạo job, không theo Settings hiện hành
            storageRootId = request.StorageRootId;
            storageRootPath = await _rootRegistry.ResolveRootPathAsync(storageRootId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var currentRoots = await _rootRegistry.GetCurrentRootsAsync(cancellationToken).ConfigureAwait(false);
            storageRootId = isVideo ? currentRoots.VideosRootId : currentRoots.ImagesRootId;
            storageRootPath = isVideo ? currentRoots.VideosPath : currentRoots.ImagesPath;
        }

        AssetFileResult saveResult;
        var assetId = Guid.NewGuid().ToString("N");
        string mimeType;

        if (!string.IsNullOrWhiteSpace(request.Base64Data))
        {
            var fileName = request.FileName;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = isAudio ? "audio.mp3" : "image.png";
            }

            extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(extension))
            {
                extension = isAudio ? ".mp3" : ".png";
            }

            if (isAudio && !AllowedAudioExtensions.Contains(extension))
            {
                throw new AssetValidationException($"Chỉ hỗ trợ tệp âm thanh .mp3 hoặc .wav. Định dạng hiện tại: '{extension}'.");
            }
            if (isImage && !AllowedImageExtensions.Contains(extension))
            {
                throw new AssetValidationException($"Chỉ hỗ trợ tệp ảnh .png, .jpg, .jpeg, .webp. Định dạng hiện tại: '{extension}'.");
            }

            var base64 = request.Base64Data;
            var commaIndex = base64.IndexOf(',');
            if (commaIndex >= 0 && base64.Substring(0, commaIndex).Contains("base64", StringComparison.OrdinalIgnoreCase))
            {
                base64 = base64.Substring(commaIndex + 1);
            }

            byte[] fileBytes;
            try
            {
                fileBytes = Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                throw new AssetValidationException("Dữ liệu tệp tải lên (Base64) không hợp lệ.", ex);
            }

            if (fileBytes.Length == 0)
            {
                throw new AssetValidationException("Tệp tải lên rỗng (0 bytes).");
            }

            var subDir = isAudio ? "audio" : "images";
            var relativePath = BuildUploadRelativePath(projectId, subDir, assetId, extension);

            using var memoryStream = new MemoryStream(fileBytes);
            saveResult = await _fileStore.SaveFileAsync(
                storageRootPath,
                relativePath,
                memoryStream,
                cancellationToken
            ).ConfigureAwait(false);

            mimeType = ResolveMimeType(extension, isAudio);

            var asset = new Asset
            {
                Id = assetId,
                ProjectId = projectId,
                AssetType = isAudio ? "Audio" : "Image",
                StorageRootId = storageRootId,
                RelativePath = relativePath,
                ByteSize = saveResult.ByteSize,
                Sha256Hash = saveResult.Sha256Hash,
                Status = "Linked",
                MimeType = mimeType,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            };

            dbContext.Assets.Add(asset);
            await SaveAssetMetadataAsync(dbContext, storageRootPath, relativePath, cancellationToken).ConfigureAwait(false);

            return new UploadAssetResponse(ToDto(asset));
        }
        else if (!string.IsNullOrWhiteSpace(request.SourceFilePath))
        {
            if (!File.Exists(request.SourceFilePath))
            {
                throw new AssetValidationException($"Tệp nguồn không tồn tại: '{request.SourceFilePath}'.");
            }

            extension = Path.GetExtension(request.SourceFilePath).ToLowerInvariant();
            if (isAudio && !AllowedAudioExtensions.Contains(extension))
            {
                throw new AssetValidationException($"Chỉ hỗ trợ tệp âm thanh .mp3 hoặc .wav. Định dạng hiện tại: '{extension}'.");
            }
            if (isImage && !AllowedImageExtensions.Contains(extension))
            {
                throw new AssetValidationException($"Chỉ hỗ trợ tệp ảnh .png, .jpg, .jpeg, .webp. Định dạng hiện tại: '{extension}'.");
            }

            if (isAudio)
            {
                ValidateAudioFile(request.SourceFilePath, extension);
            }
            if (isVideo) ReferenceVideoMedia.ReadInfo(request.SourceFilePath);

            var subDir = isAudio ? "audio" : isVideo ? "videos" : "images";
            var relativePath = BuildUploadRelativePath(projectId, subDir, assetId, extension);

            saveResult = await _fileStore.SaveFileFromPathAsync(
                storageRootPath,
                relativePath,
                request.SourceFilePath,
                cancellationToken
            ).ConfigureAwait(false);

            mimeType = isVideo ? "video/mp4" : ResolveMimeType(extension, isAudio);

            var asset = new Asset
            {
                Id = assetId,
                ProjectId = projectId,
                AssetType = isAudio ? "Audio" : isVideo ? "Video" : "Image",
                StorageRootId = storageRootId,
                RelativePath = relativePath,
                ByteSize = saveResult.ByteSize,
                Sha256Hash = saveResult.Sha256Hash,
                Status = "Linked",
                MimeType = mimeType,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            };

            dbContext.Assets.Add(asset);
            await SaveAssetMetadataAsync(dbContext, storageRootPath, relativePath, cancellationToken).ConfigureAwait(false);

            return new UploadAssetResponse(ToDto(asset));
        }
        else
        {
            throw new AssetValidationException("Cần cung cấp dữ liệu tệp (Base64 hoặc SourceFilePath) để tải lên tài nguyên.");
        }
    }

    private static string BuildUploadRelativePath(string? projectId, string subDir, string assetId, string extension)
    {
        if (projectId is not null) return $"{projectId}/{subDir}/{assetId}{extension}";
        return $"library/{subDir}/{assetId}{extension}";
    }

    private static string ResolveMimeType(string extension, bool isAudio)
    {
        if (isAudio)
        {
            return extension == ".mp3" ? "audio/mpeg" : "audio/wav";
        }

        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Lấy metadata chi tiết của một Asset theo AssetId.
    /// </summary>
    public async Task<AssetDto> GetMetadataAsync(
        GetAssetMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AssetId))
        {
            throw new ArgumentException("Mã assetId không được để trống.", nameof(request.AssetId));
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.AssetId, cancellationToken)
            .ConfigureAwait(false);

        if (asset is null)
        {
            throw new AssetNotFoundException(request.AssetId);
        }

        return ToDto(asset);
    }

    public async Task<(Stream Content, string ContentType)> OpenPreviewAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var asset = await GetMetadataAsync(new GetAssetMetadataRequest(assetId), cancellationToken).ConfigureAwait(false);
        var contentType = Path.GetExtension(asset.RelativePath).ToLowerInvariant() switch
        {
            ".png" when asset.AssetType == "Image" => "image/png",
            ".jpg" or ".jpeg" when asset.AssetType == "Image" => "image/jpeg",
            ".webp" when asset.AssetType == "Image" => "image/webp",
            ".mp3" when asset.AssetType == "Audio" => "audio/mpeg",
            ".wav" when asset.AssetType == "Audio" => "audio/wav",
            ".mp4" when asset.AssetType == "Video" => "video/mp4",
            ".webm" when asset.AssetType == "Video" => "video/webm",
            _ => throw new AssetValidationException("Định dạng tệp không hỗ trợ xem trước.")
        };
        var root = await _rootRegistry.ResolveRootPathAsync(asset.StorageRootId, cancellationToken).ConfigureAwait(false);
        var path = StoragePathGuard.CombineAndVerify(root, asset.RelativePath);
        return (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), contentType);
    }

    /// <summary>
    /// Mở thư mục chứa Asset trên hệ điều hành Windows an toàn.
    /// </summary>
    public async Task<OpenFolderResponse> OpenFolderAsync(
        OpenFolderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AssetId))
        {
            throw new ArgumentException("Mã assetId không được để trống.", nameof(request.AssetId));
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.AssetId, cancellationToken)
            .ConfigureAwait(false);

        if (asset is null)
        {
            throw new AssetNotFoundException(request.AssetId);
        }

        var rootPath = await _rootRegistry.ResolveRootPathAsync(asset.StorageRootId, cancellationToken).ConfigureAwait(false);
        var fullPath = StoragePathGuard.CombineAndVerify(rootPath, asset.RelativePath);
        var folderPath = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        {
            return new OpenFolderResponse(Success: false, FolderPath: folderPath);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Bỏ qua lỗi gọi shell nếu môi trường không cho phép
            }
        }

        return new OpenFolderResponse(Success: true, FolderPath: folderPath);
    }

    public static AssetDto ToDto(Asset asset)
    {
        return new AssetDto(
            AssetId: asset.Id,
            ProjectId: asset.ProjectId,
            AssetType: asset.AssetType,
            StorageRootId: asset.StorageRootId,
            RelativePath: asset.RelativePath,
            ByteSize: asset.ByteSize,
            Sha256Hash: asset.Sha256Hash,
            Status: asset.Status,
            CreatedAtUtc: asset.CreatedAtUtc,
            MimeType: asset.MimeType
        );
    }

    /// <summary>
    /// Liệt kê tài nguyên (Assets) có phân trang, lọc theo Project, loại và từ khóa tìm kiếm.
    /// </summary>
    public async Task<ListAssetsResponse> ListAssetsAsync(
        ListAssetsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = request.Page <= 0 ? 1 : request.Page;
        var pageSize = request.PageSize <= 0 ? 20 : (request.PageSize > 100 ? 100 : request.PageSize);

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var query = dbContext.Assets.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            var projectId = NormalizeGuid(request.ProjectId, nameof(request.ProjectId));
            query = query.Where(a => a.ProjectId == projectId || (request.IncludeShared && a.ProjectId == null));
        }

        if (!string.IsNullOrWhiteSpace(request.AssetType) &&
            !request.AssetType.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var assetType = request.AssetType.Trim();
            query = query.Where(a => EF.Functions.Like(a.AssetType, assetType));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = $"%{request.Search.Trim()}%";
            query = query.Where(a => EF.Functions.Like(a.RelativePath, search) || EF.Functions.Like(a.Id, search));
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var dtos = items.Select(ToDto).ToList();

        return new ListAssetsResponse(
            Assets: dtos,
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize
        );
    }

    /// <summary>
    /// Lấy thông tin preview chi tiết và trạng thái kiểm tra tệp vật lý (tồn tại hay root offline).
    /// </summary>
    public async Task<GetAssetPreviewResponse> GetAssetPreviewAsync(
        GetAssetPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AssetId))
        {
            throw new ArgumentException("Mã assetId không được để trống.", nameof(request.AssetId));
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.AssetId, cancellationToken)
            .ConfigureAwait(false);

        if (asset is null)
        {
            throw new AssetNotFoundException(request.AssetId);
        }

        bool isRootOffline = false;
        bool fileExists = false;

        try
        {
            var rootPath = await _rootRegistry.ResolveRootPathAsync(asset.StorageRootId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                isRootOffline = true;
            }
            else
            {
                var fullPath = StoragePathGuard.CombineAndVerify(rootPath, asset.RelativePath);
                fileExists = File.Exists(fullPath);
            }
        }
        catch (KeyNotFoundException)
        {
            isRootOffline = true;
        }
        catch (DirectoryNotFoundException)
        {
            isRootOffline = true;
        }
        catch
        {
            fileExists = false;
        }

        return new GetAssetPreviewResponse(
            AssetId: asset.Id,
            AssetType: asset.AssetType,
            RelativePath: asset.RelativePath,
            MimeType: asset.MimeType,
            ByteSize: asset.ByteSize,
            Sha256Hash: asset.Sha256Hash,
            FileExists: fileExists,
            IsRootOffline: isRootOffline,
            Status: asset.Status
        );
    }

    private static async Task VerifySceneOwnershipAsync(
        FrayloDbContext dbContext,
        string projectId,
        string sceneId,
        CancellationToken cancellationToken)
    {
        var projectExists = await dbContext.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId, cancellationToken)
            .ConfigureAwait(false);

        if (!projectExists)
        {
            throw new ProjectNotFoundException(projectId);
        }

        var sceneExists = await dbContext.Scenes
            .AsNoTracking()
            .AnyAsync(s => s.Id == sceneId && s.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);

        if (!sceneExists)
        {
            throw new SceneNotFoundException(sceneId);
        }
    }

    private const long MaxAudioFileSizeBytes = 100 * 1024 * 1024; // 100 MB

    private static void ValidateAudioFile(string filePath, string ext)
    {
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            throw new AssetValidationException("Tệp âm thanh nguồn rỗng (0 bytes).");
        }

        if (fileInfo.Length > MaxAudioFileSizeBytes)
        {
            throw new AssetValidationException($"Kích thước tệp âm thanh ({fileInfo.Length / (1024 * 1024)} MB) vượt quá giới hạn cho phép (tối đa 100 MB).");
        }

        byte[] header = new byte[12];
        int bytesRead;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bytesRead = fs.Read(header, 0, header.Length);
        }

        if (ext == ".wav")
        {
            if (bytesRead < 12 ||
                header[0] != 0x52 || header[1] != 0x49 || header[2] != 0x46 || header[3] != 0x46 || // "RIFF"
                header[8] != 0x57 || header[9] != 0x41 || header[10] != 0x56 || header[11] != 0x45)  // "WAVE"
            {
                throw new AssetValidationException("Tệp không đúng định dạng WAV hợp lệ (thiếu tiêu đề RIFF/WAVE).");
            }
        }
        else if (ext == ".mp3")
        {
            bool isId3 = bytesRead >= 3 && header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33; // "ID3"
            bool isMpegSync = bytesRead >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0; // MPEG sync frame
            if (!isId3 && !isMpegSync)
            {
                throw new AssetValidationException("Tệp không đúng định dạng MP3 hợp lệ (thiếu ID3 hoặc MPEG audio sync frame).");
            }
        }
    }

    private static string NormalizeGuid(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Tham số '{paramName}' không được để trống.", paramName);
        }

        if (Guid.TryParse(value, out var parsed))
        {
            return parsed.ToString("D").ToLowerInvariant();
        }

        return value.Trim();
    }
}
