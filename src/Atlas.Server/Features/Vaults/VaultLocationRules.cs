using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Platform.Storage;

namespace Atlas.Server.Features.Vaults;

/// <summary>Validation shared by saving and testing a location, and the storage options of a stored location.</summary>
public static class VaultLocationRules
{
    public const int RootMaxLength = 1000;

    /// <summary>
    /// Kind, root and credentials of a form, trimmed and checked by the storage parser; 400 when invalid. A managed
    /// location keeps no root or credentials, and a local folder must lie outside wwwroot.
    /// </summary>
    public static (string Kind, string Root, string Username, string Domain) Normalize(string? kind, string? root, string? username, string? domain, VaultFolders folders)
    {
        if (kind == VaultLocation.ManagedKind)
        {
            return (kind, "", "", "");
        }
        if (kind is not (VaultLocation.LocalKind or VaultLocation.SmbKind))
        {
            throw new ApiException(400, "Typ umístění musí být složka spravovaná Atlasem, místní složka, nebo síťová složka SMB.");
        }
        var value = root?.Trim() ?? "";
        var smb = kind == VaultLocation.SmbKind;
        if (value.Length == 0 || value.Length > RootMaxLength || smb != value.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidRoot();
        }
        var user = smb ? username?.Trim() ?? "" : "";
        var userDomain = smb ? domain?.Trim() ?? "" : "";
        if (user.Length > 200 || userDomain.Length > 200)
        {
            throw new ApiException(400, "Uživatelské jméno a doména smí mít nejvýše 200 znaků.");
        }
        try
        {
            FileStorages.Open(new FileStorageOptions(value, user, userDomain, null)).Dispose();
        }
        catch (ArgumentException)
        {
            throw InvalidRoot();
        }
        if (!smb && folders.IsPublished(value))
        {
            throw new ApiException(400, "Složka trezorů nesmí ležet v publikovaném obsahu wiki (wwwroot), odkud by šly soubory stáhnout.");
        }
        return (kind, value, user, userDomain);
    }

    /// <summary>Storage options of a stored location with its decrypted password (in memory only).</summary>
    public static FileStorageOptions Storage(VaultLocation location, VaultProtection protection, VaultFolders folders)
    {
        string? password = null;
        if (location.Kind == VaultLocation.SmbKind && location.ProtectedPassword is not null)
        {
            password = protection.UnprotectLocationPassword(location.ProtectedPassword)
                ?? throw new ApiException(400, Runtime.VaultFailures.LocationPassword);
        }
        return new FileStorageOptions(folders.Root(location), location.Username, location.Domain, password);
    }

    private static ApiException InvalidRoot() =>
        new(400, "Zadejte absolutní místní složku serveru, nebo adresu smb://server/sdileni/slozka. Přihlašovací údaje patří do samostatných polí; cesty UNC a vlastní porty SMB podporované nejsou.");
}
