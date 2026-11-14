using Atlas.Server.Platform.Storage;

namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>
/// Authorizes listing a location's files: the wiki administrator, or with <paramref name="SpaceId"/> the Admin of the space a vault is
/// being attached to (the same authority as attaching). Resolves the storage and the files current vaults already use.
/// </summary>
public sealed record PrepareLocationListing(Guid LocationId, Guid? SpaceId);

/// <summary>Registered file names (case-insensitive) with the vault name the caller may see, or null.</summary>
public sealed record LocationListingTarget(FileStorageOptions Storage, IReadOnlyDictionary<string, string?> Registered);
