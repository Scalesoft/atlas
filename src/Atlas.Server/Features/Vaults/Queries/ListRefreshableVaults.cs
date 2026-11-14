namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>
/// System query of the background refresh (no actor): current vaults with a stored password that are unlocked or
/// failed. Locked vaults wait until an administrator enters the password again.
/// </summary>
public sealed record ListRefreshableVaults;
