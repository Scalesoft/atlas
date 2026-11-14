using System.Globalization;
using System.Security.Cryptography;

namespace Atlas.Server.Features.Attachments;

/// <summary>Blob storage for attachments: a dedicated directory outside wwwroot, one file per attachment id. Space card images
/// (SpaceCover.FileId) share it, so they follow the configured Attachments:DataPath and its volume. A file that is not an image is
/// kept as opaque bytes by design: only its size is limited and its SHA-256 recorded, and it is always served as application/octet-stream with
/// nosniff and a sandboxing CSP, so its content is never interpreted by Atlas or the browser.</summary>
public sealed class AttachmentFiles
{
    public const long MaximumFileBytes = 25 * 1024 * 1024;
    public const long MaximumImageBytes = 10 * 1024 * 1024;

    public AttachmentFiles(IConfiguration configuration, IWebHostEnvironment environment)
    {
        Root = Path.GetFullPath(configuration["Attachments:DataPath"] ?? Path.Combine(environment.ContentRootPath, ".data", "attachments"));
        var web = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Root.Equals(Path.GetPathRoot(Root), comparison) || Root.Equals(web, comparison) || Root.StartsWith(web + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("Attachments:DataPath must be a dedicated directory outside wwwroot.");
        }
    }

    public string Root { get; }

    public string PathFor(Guid id)
    {
        return Path.Combine(Root, id.ToString("N"));
    }

    public void Remove(Guid id)
    {
        File.Delete(PathFor(id));
    }

    public FileStream Open(Guid id)
    {
        return new FileStream(PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <param name="name">The file name a non-image is typed by (its extension); the content stays opaque and is always served as octet-stream.</param>
    public async Task<(long Size, string Hash, string MimeType)> WriteAsync(Guid id, Stream input, bool image, CancellationToken ct, string? name = null)
    {
        Directory.CreateDirectory(Root);
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Attachment storage must not be a symbolic link.");
        }
        var path = PathFor(id);
        var created = false;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 65536
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using var file = new FileStream(path, options);
            created = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            var prefix = new byte[32];
            var prefixSize = 0;
            long size = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }
                size += read;
                if (size > (image ? MaximumImageBytes : MaximumFileBytes))
                {
                    throw new ApiException(413, "Příloha překračuje povolenou velikost.");
                }
                var take = Math.Min(read, prefix.Length - prefixSize);
                buffer.AsSpan(0, take).CopyTo(prefix.AsSpan(prefixSize));
                prefixSize += take;
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            var mime = image ? DetectImage(prefix.AsSpan(0, prefixSize)) : MimeTypeOf(name);
            await file.FlushAsync(ct);
            file.Flush(flushToDisk: true);
            return (size, Convert.ToHexStringLower(hash.GetHashAndReset()), mime);
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

    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider s_types = new();

    /// <summary>The type a file name's extension names (.txt is text/plain), application/octet-stream for an unknown one. A label only: it never decides how the file is served.</summary>
    public static string MimeTypeOf(string? name) => name is not null && s_types.TryGetContentType(name, out var type) ? type : "application/octet-stream";

    public static string DetectImage(ReadOnlySpan<byte> bytes)
    {
        return TryDetectImage(bytes)
            ?? throw new ApiException(400, "Obrázek musí být PNG, JPEG, GIF nebo WebP. Obsah souboru neodpovídá podporovanému formátu.");
    }

    /// <summary>The image type by signature (the first 32 bytes suffice), or null for anything else, SVG included.</summary>
    public static string? TryDetectImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return "image/png";
        }
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 13 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }
        if (bytes.Length >= 16 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)
            && (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) || bytes.Slice(12, 4).SequenceEqual("VP8L"u8) || bytes.Slice(12, 4).SequenceEqual("VP8X"u8)))
        {
            return "image/webp";
        }
        return null;
    }

    public static string SafeName(string input)
    {
        var name = input.Replace('\\', '/').Split('/').Last();
        // Control characters and every format character (Cf: the bidi controls, zero-width and invisible characters) go: the name
        // is shown in the UI and sent in Content-Disposition, and must read as what it is ("faktura" + RLO + "fdp.exe" would not).
        name = new string(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        if (name.Length > 240)
        {
            name = name[..240];
        }
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "attachment" : name;
    }
}
