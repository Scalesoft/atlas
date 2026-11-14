namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>A vault location. Root and credentials fields are filled only for the wiki administrator; the password never leaves the server.</summary>
public sealed record VaultLocationView(Guid Id, string Name, string Kind, string? Root, string? Username, string? Domain, bool HasPassword, int VaultCount);
