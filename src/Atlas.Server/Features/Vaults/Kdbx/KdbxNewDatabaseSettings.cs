namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>Argon2id cost of a database created by Atlas. Defaults match current KeePass recommendations.</summary>
public sealed record KdbxNewDatabaseSettings
{
    public ulong Argon2MemoryBytes { get; init; } = 64UL * 1024 * 1024;

    public ulong Argon2Iterations { get; init; } = 3;

    public uint Argon2Parallelism { get; init; } = 2;
}
