using System.Security.Cryptography;
using System.Text;

namespace Atlas.Server.Features.ExternalSharing.Domain;

public static class ShareSecret
{
    public static string Create() => "sas_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret)
    {
        if (secret.Length != 68 || !secret.StartsWith("sas_", StringComparison.Ordinal)
            || secret.AsSpan(4).ContainsAnyExcept("0123456789abcdef"))
            throw ExternalSharingAccess.Missing();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }
}
