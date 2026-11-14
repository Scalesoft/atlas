using System.Security.Cryptography;

namespace Atlas.Server.Platform.Storage;

public static class FileStorages
{
    private const int ChunkBytes = 65536;

    public static IFileStorage Open(FileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = options.Location;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw InvalidLocation();
        }
        if (path.StartsWith("smb:", StringComparison.OrdinalIgnoreCase))
        {
            return new SmbFileStorage(options);
        }
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            throw InvalidLocation();
        }
        return new LocalFileStorage(Path.GetFullPath(path));
    }

    internal static ArgumentException InvalidLocation() => new("Use an absolute local directory or smb://host/share/folder. Put credentials in the separate credential fields; UNC paths and custom SMB ports are not supported.");

    internal static InvalidDataException TooLarge() => new("File exceeds the permitted read size.");

    /// <summary>The listed entries in name order, cut to the limit; skips names a later open would refuse.</summary>
    internal static FileStorageListing Listing(IEnumerable<FileStorageEntry> entries, string extension, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var matching = entries
            .Where(entry => entry.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && !HasInvalidPortableName(entry.Name) && !IsReservedDeviceName(entry.Name))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FileStorageListing(matching.Take(limit).ToArray(), matching.Count > limit);
    }

    internal static void ValidateName(string name)
    {
        if (HasInvalidPortableName(name))
        {
            throw new ArgumentException("Filename must be a single portable filename without path separators or reserved characters.", nameof(name));
        }
        if (IsReservedDeviceName(name))
        {
            throw new ArgumentException("Filename cannot use a reserved device name.", nameof(name));
        }
    }

    internal static void ValidateReadLimit(long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, Array.MaxLength);
    }

    /// <summary>Copies a stream into one array and fails as soon as more than the limit was read.</summary>
    internal static async Task<byte[]> ReadBoundedAsync(Stream source, long maximumBytes, long lengthHint, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream((int)Math.Clamp(lengthHint, 0, maximumBytes));
        var chunk = new byte[ChunkBytes];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (count > maximumBytes - total)
            {
                throw TooLarge();
            }
            buffer.Write(chunk, 0, count);
            total += count;
        }
        return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
    }

    internal static async Task TestAsync(IFileStorage storage, CancellationToken cancellationToken)
    {
        var name = $"atlas-probe-{Guid.NewGuid():N}.tmp";
        var expected = RandomNumberGenerator.GetBytes(128);
        var published = false;
        try
        {
            using var content = new MemoryStream(expected, false);
            await storage.PublishNewAsync(name, content, cancellationToken);
            published = true;
            await using var read = await storage.OpenReadAsync(name, cancellationToken);
            var actual = new byte[expected.Length];
            await read.ReadExactlyAsync(actual, cancellationToken);
            if (!actual.AsSpan().SequenceEqual(expected) || read.ReadByte() != -1)
            {
                throw new IOException("Storage returned different data. Check the storage server and available space.");
            }
        }
        finally
        {
            if (published)
            {
                await storage.DeleteAsync(name, CancellationToken.None);
            }
        }
    }

    private static bool HasInvalidPortableName(string name) =>
        string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
        || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c));

    private static bool IsReservedDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]));
    }
}
