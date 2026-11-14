namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>
/// A KeePass file in a location's folder. Registered tells that a current vault already uses it; UsedBy names that vault only
/// when the caller manages its space (a WikiAdmin always), so the listing never reveals vault names of other spaces.
/// </summary>
public sealed record VaultLocationFile(string Name, long SizeBytes, DateTime LastWriteAt, bool Registered, string? UsedBy);

/// <summary>The *.kdbx files of a location's root folder (one level), ordered by name; Truncated when more matched than listed.</summary>
public sealed record VaultLocationFiles(IReadOnlyList<VaultLocationFile> Files, bool Truncated);
