namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// Read-only view of the decompressed payload that counts bytes actually produced, so a small compressed
/// file cannot expand past <see cref="VaultOptions.MaxDecompressedBytes"/>. Once <see cref="GuardMarkup"/> is
/// set (the XML part), it also bounds the raw bytes between two <c>&lt;</c> characters. XmlReader materializes a
/// whole start tag or text node, so this caps the memory of any single node, attributes included.
/// </summary>
internal sealed class KdbxBoundedStream : Stream
{
    public const int MaximumChunkBytes = 16 * 1024;

    private readonly Stream m_inner;
    private readonly long m_maximumBytes;
    private readonly long m_maximumNodeBytes;
    private long m_total;
    private long m_sinceMarkup;

    public KdbxBoundedStream(Stream inner, long maximumBytes, long maximumNodeBytes)
    {
        m_inner = inner;
        m_maximumBytes = maximumBytes;
        m_maximumNodeBytes = maximumNodeBytes;
    }

    public bool GuardMarkup { get; set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => m_total;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        // Chunks stay below the smallest node limit (above 64 KiB), which the markup guard relies on.
        var read = m_inner.Read(buffer[..Math.Min(buffer.Length, MaximumChunkBytes)]);
        m_total += read;
        if (m_total > m_maximumBytes)
        {
            throw KdbxException.Limit("decompressed size", nameof(VaultOptions.MaxDecompressedBytes));
        }
        if (GuardMarkup && read > 0)
        {
            var chunk = buffer[..read];
            var first = chunk.IndexOf((byte)'<');
            if (first < 0)
            {
                m_sinceMarkup += read;
            }
            else
            {
                // Runs between two '<' inside one chunk are shorter than the chunk and thus than the limit.
                if (m_sinceMarkup + first > m_maximumNodeBytes)
                {
                    throw KdbxException.Limit("XML field size", nameof(VaultOptions.MaxXmlFieldBytes));
                }
                m_sinceMarkup = read - chunk.LastIndexOf((byte)'<') - 1;
            }
            if (m_sinceMarkup > m_maximumNodeBytes)
            {
                throw KdbxException.Limit("XML field size", nameof(VaultOptions.MaxXmlFieldBytes));
            }
        }
        return read;
    }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes; a short payload is corrupt.</summary>
    public void ReadExact(Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var read = Read(buffer);
            if (read == 0)
            {
                throw KdbxException.Corrupt();
            }
            buffer = buffer[read..];
        }
    }

    /// <summary>Discards <paramref name="count"/> bytes in small chunks, counting them against the limit.</summary>
    public void Skip(long count, CancellationToken cancellationToken)
    {
        Span<byte> scratch = stackalloc byte[4096];
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = scratch[..(int)Math.Min(scratch.Length, count)];
            ReadExact(chunk);
            count -= chunk.Length;
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
