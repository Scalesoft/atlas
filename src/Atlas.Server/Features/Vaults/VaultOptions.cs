namespace Atlas.Server.Features.Vaults;

/// <summary>
/// Resource limits for reading KeePass files, configured under <c>Vaults:*</c>. Every limit is checked before
/// the work it bounds: file and header sizes before parsing, KDF cost before key derivation, decompressed
/// size and XML shape while streaming. Exceeding one fails with <c>KdbxFailure.LimitExceeded</c>.
/// </summary>
public sealed class VaultOptions
{
    public const string Section = "Vaults";

    public long MaxFileBytes { get; set; } = 16L * 1024 * 1024;

    public int MaxHeaderBytes { get; set; } = 64 * 1024;

    public int MaxHeaderFieldBytes { get; set; } = 16 * 1024;

    public long MaxArgon2MemoryBytes { get; set; } = 256L * 1024 * 1024;

    public long MaxArgon2Iterations { get; set; } = 32;

    public int MaxArgon2Parallelism { get; set; } = 8;

    public long MaxAesKdfRounds { get; set; } = 60_000_000;

    public long MaxDecompressedBytes { get; set; } = 64L * 1024 * 1024;

    public int MaxGroupDepth { get; set; } = 64;

    public int MaxEntries { get; set; } = 50_000;

    public int MaxXmlFieldBytes { get; set; } = 1024 * 1024;

    /// <summary>How often unlocked vaults are checked for changes; 0 turns the background refresh off (tests).</summary>
    public int RefreshSeconds { get; set; } = 60;

    public static VaultOptions Read(IConfiguration configuration)
    {
        var options = new VaultOptions();
        configuration.GetSection(Section).Bind(options);
        options.Validate();
        return options;
    }

    public void Validate()
    {
        // Upper bounds keep every limit representable where it is used: arrays, Argon2 KiB as int, lanes.
        Check(MaxFileBytes, Array.MaxLength, nameof(MaxFileBytes));
        Check(MaxHeaderBytes, 1024 * 1024, nameof(MaxHeaderBytes));
        Check(MaxHeaderFieldBytes, MaxHeaderBytes, nameof(MaxHeaderFieldBytes));
        Check(MaxArgon2MemoryBytes, 4L * 1024 * 1024 * 1024, nameof(MaxArgon2MemoryBytes));
        Check(MaxArgon2Iterations, int.MaxValue, nameof(MaxArgon2Iterations));
        Check(MaxArgon2Parallelism, 255, nameof(MaxArgon2Parallelism));
        Check(MaxAesKdfRounds, long.MaxValue, nameof(MaxAesKdfRounds));
        Check(MaxDecompressedBytes, long.MaxValue, nameof(MaxDecompressedBytes));
        Check(MaxGroupDepth, 1024, nameof(MaxGroupDepth));
        Check(MaxEntries, 10_000_000, nameof(MaxEntries));
        Check(MaxXmlFieldBytes, 64 * 1024 * 1024, nameof(MaxXmlFieldBytes));
        if (RefreshSeconds is < 0 or > 86400)
        {
            throw new InvalidOperationException($"{Section}:{nameof(RefreshSeconds)} must be between 0 and 86400.");
        }
    }

    private static void Check(long value, long maximum, string name)
    {
        if (value <= 0 || value > maximum)
        {
            throw new InvalidOperationException($"{Section}:{name} must be between 1 and {maximum}.");
        }
    }
}
