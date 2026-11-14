namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>Registry entries of one vault the actor may Discover: titles, paths and available fields, never values.</summary>
public sealed record ListVaultSecrets(Guid VaultId);
