using System.IO;

namespace Fraylo.Desktop.Hosting;

// WebView2 reads the response asynchronously and does not dispose its stream.
internal sealed class PreviewStream : Stream
{
    private readonly Stream _source;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public PreviewStream(Stream source, long start, long length)
    {
        _source = source;
        _start = start;
        _length = length;
        _source.Position = start;
    }

    ~PreviewStream() => _source.Dispose();
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        try
        {
            var read = remaining <= 0 ? 0 : _source.Read(buffer, offset, (int)Math.Min(count, remaining));
            _position += read;
            if (read == 0 && count > 0) _source.Dispose();
            return read;
        }
        catch
        {
            _source.Dispose();
            throw;
        }
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => _length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (position < 0 || position > _length) throw new IOException("Vị trí ngoài phạm vi xem trước.");
        _source.Position = _start + position;
        return _position = position;
    }
    protected override void Dispose(bool disposing) { if (disposing) _source.Dispose(); base.Dispose(disposing); GC.SuppressFinalize(this); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
