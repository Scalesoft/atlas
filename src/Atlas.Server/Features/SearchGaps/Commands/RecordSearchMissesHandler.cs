using System.Security.Cryptography;
using System.Text;

namespace Atlas.Server.Features.SearchGaps.Commands;

/// <summary>Stores committed misses without identity (D9) through <see cref="SearchMissWriter"/>. Runs in a system scope with no
/// HTTP actor and returns nothing.</summary>
public sealed class RecordSearchMissesHandler(SearchMissWriter writer)
{
    internal const string Purpose = "ScaleAtlas.SearchGaps.UserKey.v1";

    public Task Handle(RecordSearchMisses command, CancellationToken ct) => writer.WriteAsync(command.Misses, ct);

    /// <summary>First 16 bytes of HMAC-SHA256(secret, user id, space id or zeros, UTF-8 normalized query). The fixed-width ids
    /// keep the input unambiguous.</summary>
    internal static byte[] UserKey(byte[] secret, Guid userId, Guid? spaceId, string normalized)
    {
        var input = new byte[32 + Encoding.UTF8.GetByteCount(normalized)];
        userId.TryWriteBytes(input.AsSpan(0, 16));
        (spaceId ?? Guid.Empty).TryWriteBytes(input.AsSpan(16, 16));
        Encoding.UTF8.GetBytes(normalized, input.AsSpan(32));
        return HMACSHA256.HashData(secret, input)[..16];
    }
}
