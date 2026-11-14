using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Platform.Storage;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults;

/// <summary>Rules for registering a vault: its name, its file name and uniqueness among current vaults.</summary>
public static class VaultRegistrations
{
    public const string Extension = ".kdbx";

    public static string RequireName(string? name) => Rules.Required(name, "Název trezoru");

    /// <summary>A single portable file name ending in .kdbx, trimmed.</summary>
    public static string RequireFileName(string? fileName)
    {
        var value = fileName?.Trim() ?? "";
        try
        {
            FileStorages.ValidateName(value);
        }
        catch (ArgumentException)
        {
            throw new ApiException(400, "Název souboru musí být jeden název bez lomítek a zakázaných znaků.");
        }
        if (!value.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || value.Length == Extension.Length)
        {
            throw new ApiException(400, "Soubor trezoru musí mít příponu .kdbx.");
        }
        return value;
    }

    public static async Task<VaultLocation> RequireLocationAsync(ISession session, Guid locationId, CancellationToken ct)
    {
        var location = await session.GetAsync<VaultLocation>(locationId, ct);
        if (location is null || location.ArchivedAt is not null)
        {
            throw new ApiException(404, "Umístění trezoru nebylo nalezeno.");
        }
        return location;
    }

    /// <summary>409 when the file is already a current vault anywhere, or the space already has a vault of that name.</summary>
    public static async Task EnsureUniqueAsync(ISession session, Guid spaceId, Guid locationId, string name, string fileName, CancellationToken ct)
    {
        var sameFile = await session.QueryOver<Vault>()
            .Where(v => v.ArchivedAt == null && v.LocationId == locationId)
            .Select(v => v.FileName)
            .ListAsync<string>(ct);
        if (sameFile.Any(existing => string.Equals(existing, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ApiException(409, "Tento soubor už je v Atlasu připojený jako trezor.", "vault_file_registered");
        }
        var sameSpace = await session.QueryOver<Vault>()
            .Where(v => v.ArchivedAt == null && v.SpaceId == spaceId)
            .Select(v => v.Name)
            .ListAsync<string>(ct);
        if (sameSpace.Any(existing => string.Equals(existing, name, StringComparison.InvariantCultureIgnoreCase)))
        {
            throw new ApiException(409, "Trezor s tímto názvem v prostoru už existuje.", "vault_name_taken");
        }
    }
}
