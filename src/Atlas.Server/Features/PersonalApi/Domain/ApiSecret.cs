using System.Security.Cryptography;
using System.Text;

namespace Atlas.Server.Features.PersonalApi.Domain;

/// <summary>Shape and one-way hash of a personal API secret. The plaintext never leaves the issuing response.</summary>
public static class ApiSecret
{
    public const string Scheme = "sat_";

    public static string Create() => Scheme + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static bool IsWellFormed(string secret)
    {
        if (secret.Length != 68 || !secret.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var character in secret.AsSpan(Scheme.Length))
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
