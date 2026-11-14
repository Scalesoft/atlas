using System.Security.Cryptography;
using System.Text;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// A decrypted field value as UTF-8 bytes. Hold it briefly: <see cref="Dispose"/> zeroes the buffer. Strings
/// returned by <see cref="Reveal"/> cannot be zeroed, so call it only at the point of release.
/// </summary>
public sealed class KdbxProtectedValue : IDisposable
{
    private byte[] m_utf8;
    private bool m_disposed;

    internal KdbxProtectedValue(byte[] utf8)
    {
        m_utf8 = utf8;
    }

    public int Length => Utf8.Length;

    public ReadOnlySpan<byte> Utf8
    {
        get
        {
            ObjectDisposedException.ThrowIf(m_disposed, this);
            return m_utf8;
        }
    }

    internal static KdbxProtectedValue Empty() => new([]);

    public string Reveal() => Encoding.UTF8.GetString(Utf8);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(m_utf8);
        m_utf8 = [];
        m_disposed = true;
    }

    public override string ToString() => "[protected]";
}
