namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>Authorizes an operation on a current vault and resolves how to reach its file.</summary>
public sealed record PrepareVault(Guid VaultId);
