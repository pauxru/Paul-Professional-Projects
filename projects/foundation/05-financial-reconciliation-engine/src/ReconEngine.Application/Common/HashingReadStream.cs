using System.Security.Cryptography;

namespace ReconEngine.Application.Common;

/// <summary>
/// A read-only pass-through stream that computes a SHA-256 digest of every byte read through it. Lets
/// the import pipeline checksum a file while it streams, without buffering the whole file in memory.
/// </summary>
public sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? _digest;

    public HashingReadStream(Stream inner) => _inner = inner;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
            _hash.AppendData(buffer, offset, read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
            _hash.AppendData(buffer.Span[..read]);
        return read;
    }

    public string GetHashHex()
    {
        _digest ??= _hash.GetHashAndReset();
        return Convert.ToHexString(_digest);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }
}
