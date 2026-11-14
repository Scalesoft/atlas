using System.Security.Cryptography;
using Atlas.Server.Features.ContentImport.Domain;
using Atlas.Server.Features.ModuleSettings;

namespace Atlas.Server.Features.ContentImport;

/// <summary>Private ZIP storage for uploaded Wiki.js export sources, capped by a shared disk quota.</summary>
public sealed class ContentImportFiles
{
    private readonly IWebHostEnvironment m_environment;

    public ContentImportFiles(IConfiguration configuration, IWebHostEnvironment environment)
    {
        m_environment = environment;
        Root = Path.GetFullPath(configuration["ContentImport:DataPath"] ?? Path.Combine(environment.ContentRootPath, ".data", "imports"));
        MaximumStoredBytes = Math.Clamp(configuration.GetValue<long?>("ContentImport:MaximumStoredBytes") ?? 4L * 1024 * 1024 * 1024, WikiJsArchive.MaximumArchiveBytes, 100L * 1024 * 1024 * 1024);
    }

    public string Root { get; }

    public long MaximumStoredBytes { get; }

    public string PathFor(Guid id)
    {
        return Path.Combine(Root, id.ToString("N") + ".zip");
    }

    public FileStream Open(Guid id)
    {
        return new FileStream(PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void Remove(Guid id)
    {
        File.Delete(PathFor(id));
    }

    public async Task<string> WriteAsync(Guid id, Stream input, CancellationToken ct)
    {
        PrivateServerFiles.PrepareDirectory(Root, m_environment);
        // Import work is serialized by the ImportWorkGate, held by the endpoint (not this handler) for the
        // whole preview/commit call; reserve one full bounded archive against the private disk quota assuming single-flight.
        if (new DirectoryInfo(Root).EnumerateFiles("*.zip").Sum(f => f.Length) + WikiJsArchive.MaximumArchiveBytes > MaximumStoredBytes)
        {
            throw new ApiException(507, "Soukromé úložiště importů je plné. Správce musí archivovat staré zdroje nebo upravit limit.");
        }
        var path = PathFor(id);
        var options = PrivateServerFiles.CreateOptions(FileMode.CreateNew);
        options.Options |= FileOptions.Asynchronous;
        var created = false;
        try
        {
            await using var file = new FileStream(path, options);
            created = true;
            PrivateServerFiles.ProtectFile(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long count = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }
                count += read;
                if (count > WikiJsArchive.MaximumArchiveBytes)
                {
                    throw new ApiException(413, "ZIP smí mít nejvýše 64 MiB.");
                }
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await file.FlushAsync(ct);
            file.Flush(true);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch
        {
            if (created)
            {
                File.Delete(path);
            }
            throw;
        }
    }
}
