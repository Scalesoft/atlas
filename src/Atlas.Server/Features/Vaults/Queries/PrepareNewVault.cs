namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>
/// Authorizes creating or attaching a vault in a space and resolves the location, before any file I/O. The file
/// work runs outside the transaction; <c>RegisterVault</c> checks everything again when it writes.
/// </summary>
public sealed record PrepareNewVault(Guid SpaceId, Guid LocationId, string Name, string FileName);
