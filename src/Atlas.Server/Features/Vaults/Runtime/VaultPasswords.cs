using System.Security.Cryptography;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>Master passwords of vaults Atlas creates, and the length rules for passwords administrators enter.</summary>
public static class VaultPasswords
{
    public const int GeneratedLength = 28;
    public const int MinimumNewLength = 12;
    public const int MaximumLength = 1024;

    // No look-alike characters (0/O, 1/l/I), so the password can be copied by hand into KeePass.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_.!+=";

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, GeneratedLength);

    /// <summary>A password for a new database: 12 to 1024 characters.</summary>
    public static string RequireNew(string? password)
    {
        if (password is null || password.Length < MinimumNewLength || password.Length > MaximumLength)
        {
            throw new ApiException(400, "Hlavní heslo nového trezoru musí mít {minimumNewLength} až {maximumLength} znaků.", args: new Dictionary<string, object?> { ["minimumNewLength"] = MinimumNewLength, ["maximumLength"] = MaximumLength });
        }
        return password;
    }

    /// <summary>The password of an existing database: whatever KeePass accepted, up to 1024 characters.</summary>
    public static string RequireExisting(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > MaximumLength)
        {
            throw new ApiException(400, "Zadejte hlavní heslo databáze KeePass (nejvýše 1024 znaků).");
        }
        return password;
    }
}
