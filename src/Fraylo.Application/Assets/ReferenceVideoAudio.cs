using System.Buffers.Binary;

namespace Fraylo.Application.Assets;

public sealed record ReferenceAudioInfo(int TrackCount, double DurationSeconds);

/// <summary>Chỉ đọc metadata để kiểm đầu ra. MuseAI thực hiện ghép/co giãn âm thanh, không xử lý media cục bộ.</summary>
public static class ReferenceVideoAudio
{
    public const string FitByAi = "FitOriginalAudioByAI";

    public static bool HasAudio(string path) => ReadInfo(path).TrackCount > 0;

    public static ReferenceAudioInfo ReadInfo(string path)
    {
        ReferenceVideoMedia.ReadInfo(path);
        try
        {
            using var stream = File.OpenRead(path);
            for (long p = 0; p < stream.Length;)
            {
                var (type, header, size) = AssetService.ReadMp4BoxHeader(stream, p);
                if (type == "moov")
                {
                    var bytes = new byte[size - header]; stream.Position = p + header; stream.ReadExactly(bytes);
                    var count = 0;
                    double duration = 0;
                    foreach (var track in AssetService.Mp4Children(bytes, 0, bytes.Length).Where(b => b.Type == "trak"))
                    {
                        var media = AssetService.Mp4Children(bytes, track.Start, track.End).FirstOrDefault(b => b.Type == "mdia");
                        var children = AssetService.Mp4Children(bytes, media.Start, media.End);
                        var handler = children.FirstOrDefault(b => b.Type == "hdlr");
                        if (handler.Type is null || handler.End - handler.Start < 12 || !bytes.AsSpan(handler.Start + 8, 4).SequenceEqual("soun"u8)) continue;
                        var mdhd = children.Single(b => b.Type == "mdhd");
                        var version = bytes[mdhd.Start];
                        if (version > 1 || mdhd.End - mdhd.Start < (version == 1 ? 32 : 20)) throw Invalid();
                        var timescale = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mdhd.Start + (version == 1 ? 20 : 12), 4));
                        var units = version == 1 ? BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(mdhd.Start + 24, 8))
                            : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mdhd.Start + 16, 4));
                        var seconds = (double)units / timescale;
                        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > ReferenceVideoMedia.MaxVideoSeconds + .25) throw Invalid();
                        count++; duration = Math.Max(duration, seconds);
                    }
                    return new ReferenceAudioInfo(count, duration);
                }
                p += size;
            }
            throw Invalid();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException or EndOfStreamException)
        { throw new AssetValidationException("Metadata âm thanh MP4 không hợp lệ.", ex); }
    }

    public static void VerifyOutput(string referencePath, string outputPath)
    {
        var source = ReadInfo(referencePath);
        var output = ReadInfo(outputPath);
        if ((source.TrackCount > 0) != (output.TrackCount > 0))
            throw new AssetValidationException("Video trả về thiếu âm thanh mẫu hoặc đã thêm âm thanh khi mẫu im lặng.");
        var video = ReferenceVideoMedia.ReadInfo(outputPath);
        if (output.TrackCount > 0 && Math.Abs(output.DurationSeconds - video.DurationSeconds) > .25)
            throw new AssetValidationException("Âm thanh trong video trả về chưa khớp độ dài phần hình.");
        // Re-encoding/time stretching is expected: metadata cannot prove the identity of a song.
    }

    private static AssetValidationException Invalid() => new("Chưa đọc được thời lượng luồng âm thanh của video.");
}
