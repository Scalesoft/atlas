namespace Atlas.Server.Features.Spaces.Contracts;

/// <param name="Name">The name of the space, typed by the wiki admin as the confirmation.</param>
public sealed record TrashSpaceRequest(string? Name);

/// <param name="PageCount">The pages trashed with the space, which its restore brings back.</param>
/// <param name="VaultNames">Its vaults, which a permanent delete archives with their secrets.</param>
public sealed record TrashedSpaceItem(Guid Id, string Name, string Icon, string Color, DateTime TrashedAt, Guid TrashedById, string TrashedByName, int PageCount,
    IReadOnlyList<string> VaultNames);

/// <param name="Name">The name of the space, typed by the wiki admin as the confirmation of the permanent delete.</param>
public sealed record PurgeSpaceRequest(string? Name);
