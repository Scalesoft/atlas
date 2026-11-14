using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Atlas.Server.Features.ContentExport.Contracts;

namespace Atlas.Server.Features.ContentExport.Domain;

/// <summary>Streams one payload into the ZIP under construction, hashing it and charging the budget as it writes.</summary>
public static class ExportWriter
{
    public static async Task<ExportFile> WriteTextAsync(ZipArchive zip, string path, string text, string mime, ExportBudget budget, CancellationToken ct)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(text), false);
        return await WriteAsync(zip, path, input, mime, budget, ct);
    }

    public static async Task<ExportFile> WriteAsync(ZipArchive zip, string path, Stream input, string mime, ExportBudget budget, CancellationToken ct)
    {
        budget.AddFile();
        await using var output = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long size = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }
            budget.AddBytes(read);
            size += read;
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return new ExportFile(path, size, Convert.ToHexStringLower(hash.GetHashAndReset()), mime);
    }
}
