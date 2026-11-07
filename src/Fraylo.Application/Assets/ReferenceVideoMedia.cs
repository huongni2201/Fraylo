using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Application.Assets;

public sealed record ReferenceVideoInfo(double DurationSeconds, int Width, int Height);

internal sealed record ReferenceVideoFiles(string ImagePath, string VideoPath, JobMediaSnapshotDto Snapshot);

public static class ReferenceVideoMedia
{
    public const int OutputSeconds = 10;
    public const string ApproximationMode = "PromptApproximation";
    public const long MaxVideoBytes = 50 * 1024 * 1024;
    public const int MaxVideoSeconds = 30;

    public static ReferenceVideoInfo ReadInfo(string path)
    {
        try { return ReadInfoCore(path); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException or EndOfStreamException)
        { throw new AssetValidationException("Cấu trúc metadata video mẫu không hợp lệ.", ex); }
    }

    private static ReferenceVideoInfo ReadInfoCore(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > MaxVideoBytes || !file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new AssetValidationException("Video mẫu phải là MP4 hợp lệ, tối đa 50 MB.");
        AssetService.VerifyPlayableMp4(path);
        using var stream = File.OpenRead(path);
        byte[]? moov = null;
        for (long offset = 0; offset < stream.Length;)
        {
            var (type, header, size) = AssetService.ReadMp4BoxHeader(stream, offset);
            if (type == "moov")
            {
                if (size - header > 4 * 1024 * 1024) throw new AssetValidationException("Metadata video mẫu quá lớn.");
                moov = new byte[size - header];
                stream.Position = offset + header;
                stream.ReadExactly(moov);
            }
            offset += size;
        }
        if (moov is null) throw new AssetValidationException("Video mẫu thiếu metadata MP4.");
        var boxes = AssetService.Mp4Children(moov, 0, moov.Length);
        var mvhd = boxes.First(b => b.Type == "mvhd");
        var version = moov[mvhd.Start];
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mvhd.Start + (version == 1 ? 20 : 12)));
        var units = version == 1 ? BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(mvhd.Start + 24))
            : BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mvhd.Start + 16));
        var duration = (double)units / timescale;
        if (!double.IsFinite(duration) || duration <= 0 || duration > MaxVideoSeconds)
            throw new AssetValidationException("Video mẫu phải có thời lượng lớn hơn 0 và không quá 30 giây.");
        foreach (var trak in boxes.Where(b => b.Type == "trak"))
        {
            var track = AssetService.Mp4Children(moov, trak.Start, trak.End);
            var mdia = track.FirstOrDefault(b => b.Type == "mdia");
            var handler = AssetService.Mp4Children(moov, mdia.Start, mdia.End).FirstOrDefault(b => b.Type == "hdlr");
            if (handler.Type == null || handler.End - handler.Start < 12 || !moov.AsSpan(handler.Start + 8, 4).SequenceEqual("vide"u8)) continue;
            var tkhd = track.FirstOrDefault(b => b.Type == "tkhd");
            if (tkhd.Type == null || tkhd.End - tkhd.Start < 84 || moov[tkhd.Start] > 1 ||
                (moov[tkhd.Start] == 1 && tkhd.End - tkhd.Start < 96)) continue;
            var width = (int)(BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(tkhd.End - 8)) >> 16);
            var height = (int)(BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(tkhd.End - 4)) >> 16);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                throw new AssetValidationException("Kích thước video mẫu không hợp lệ hoặc vượt 4096 px.");
            return new ReferenceVideoInfo(duration, width, height);
        }
        throw new AssetValidationException("Video mẫu không có track hình ảnh với kích thước hợp lệ.");
    }

    internal static async Task<ReferenceVideoFiles> ValidateAsync(
        DbContextOptions<FrayloDbContext> options, string projectId, JobMediaSnapshotDto media,
        bool requireSnapshotHashes, CancellationToken cancellationToken)
    {
        var imageId = media.SourceImageAssetId ?? media.ReadyImageAssetId;
        if (string.IsNullOrWhiteSpace(imageId) || string.IsNullOrWhiteSpace(media.ReferenceVideoAssetId) ||
            media.ReferenceVideoMode != ApproximationMode || media.ReferenceAudioPolicy != ReferenceVideoAudio.FitByAi ||
            (media.ImageAssetIds is not null && !media.ImageAssetIds.SequenceEqual(new[] { imageId })))
            throw new JobValidationException("Thiếu hoặc sai vai trò ảnh và video mẫu trong snapshot.");
        await using var db = new FrayloDbContext(options);
        var roots = new StorageRootRegistry(options);
        async Task<(string Path, string Hash)> ResolveAsync(string id, string type, string? expectedHash)
        {
            var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (asset == null || asset.AssetType != type || asset.Status != "Linked" ||
                (asset.ProjectId is not null && asset.ProjectId != projectId))
                throw new JobValidationException($"Tệp {type} không hợp lệ hoặc không thuộc dự án này.");
            var root = await roots.ResolveRootPathAsync(asset.StorageRootId, cancellationToken);
            string path;
            try { path = StoragePathGuard.CombineAndVerify(root, asset.RelativePath); }
            catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException)
            { throw new JobValidationException($"Tệp {type} nằm ngoài thư mục lưu trữ được cấp."); }
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > (type == "Video" ? MaxVideoBytes : 20 * 1024 * 1024))
                throw new JobValidationException($"Tệp {type} bị thiếu, thư mục không khả dụng hoặc dung lượng vượt giới hạn.");
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(asset.Sha256Hash, StringComparison.OrdinalIgnoreCase) ||
                (requireSnapshotHashes && (string.IsNullOrWhiteSpace(expectedHash) || !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))))
                throw new JobValidationException($"Tệp {type} đã thay đổi so với dữ liệu hoặc snapshot của job.");
            if (type == "Image")
            {
                stream.Position = 0;
                var header = new byte[12];
                if (await stream.ReadAsync(header, cancellationToken) < header.Length ||
                    !(header.AsSpan(0, 3).SequenceEqual(new byte[] { 255, 216, 255 }) ||
                      header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                      (header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8))))
                    throw new JobValidationException("Ảnh người mẫu phải là JPEG, PNG hoặc WebP hợp lệ.");
            }
            return (path, hash);
        }
        var image = await ResolveAsync(imageId, "Image", media.SourceImageAssetHash ?? media.ReadyImageAssetHash);
        var video = await ResolveAsync(media.ReferenceVideoAssetId, "Video", media.ReferenceVideoAssetHash);
        ReferenceVideoInfo metadata;
        try { metadata = ReadInfo(video.Path); }
        catch (AssetValidationException ex) { throw new JobValidationException(ex.Message); }
        return new ReferenceVideoFiles(image.Path, video.Path, media with
        {
            SourceImageAssetId = imageId,
            SourceImageAssetHash = image.Hash,
            ReadyImageAssetHash = image.Hash,
            ReferenceVideoAssetHash = video.Hash,
            ReferenceVideoDurationSeconds = metadata.DurationSeconds,
            ReferenceVideoWidth = metadata.Width,
            ReferenceVideoHeight = metadata.Height,
            ReferenceVideoHasAudio = ReferenceVideoAudio.HasAudio(video.Path),
            ReferenceAudioPolicy = ReferenceVideoAudio.FitByAi,
            DurationSeconds = OutputSeconds
        });
    }

    internal static void EnsureProviderSupported(JobMediaSnapshotDto media)
    {
        var capability = Fraylo.Application.Providers.ProviderCapabilities.GetDefaultCapability("museai", JobTaskType.VideoRender);
        if (capability?.SupportsReferenceVideoApproximation != true ||
            (media.ReferenceVideoHasAudio != false && capability.SupportsReferenceVideoAudioFit != true))
            throw new JobValidationException("MuseAI chưa xác minh tự ghép/co giãn âm thanh từ video mẫu. Bạn vẫn có thể lưu nháp.");
    }
}
