using System.Globalization;
using System.Security.Cryptography;
using Atlas.Server.Features.ModuleSettings;

namespace Atlas.Server.Features.ContentExport;

/// <summary>
/// The print-size copies <see cref="PdfImageScaler"/> made, kept between exports so each image is scaled once: a long
/// manual's scaling is most of its export and, on a busy server, more than the whole deadline. A copy is named by the
/// SHA-256 of the original's bytes and the size it was scaled to, so it can only ever stand for exactly that original at
/// exactly that size, and a timed-out export keeps the copies it finished for the next attempt. Private server files next
/// to the export staging; a copy unused for <see cref="Retention"/> is deleted, and the oldest go first above
/// <see cref="MaximumBytes"/>.
/// </summary>
public sealed class PdfPrintImageCache(string directory, IHostEnvironment environment)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public const long MaximumBytes = 1024L * 1024 * 1024;

    public static string Key(byte[] original, int width, int height, double scale, double turnedScale)
    {
        var (scaledWidth, scaledHeight) = PdfImagePlan.Size(width, height, scale);
        var (turnedWidth, turnedHeight) = PdfImagePlan.Size(height, width, turnedScale);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Convert.ToHexStringLower(SHA256.HashData(original))}-{scaledWidth}x{scaledHeight}-{turnedWidth}x{turnedHeight}");
    }

    public (byte[] Bytes, string MimeType)? Get(string key)
    {
        foreach (var (extension, mimeType) in Types)
        {
            var path = Path.Combine(directory, key + extension);
            try
            {
                var bytes = File.ReadAllBytes(path);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return (bytes, mimeType);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Stores a copy; a cache that cannot be written only costs the next export the scaling again.</summary>
    public void Put(string key, byte[] bytes, string mimeType)
    {
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            PrivateServerFiles.PrepareDirectory(directory, environment);
            using (var stream = new FileStream(temporary, PrivateServerFiles.CreateOptions(FileMode.CreateNew))) stream.Write(bytes);
            PrivateServerFiles.ProtectFile(temporary);
            File.Move(temporary, Path.Combine(directory, key + (mimeType == "image/jpeg" ? ".jpg" : ".png")), true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes copies unused for <see cref="Retention"/>, then the oldest while the rest exceed <see cref="MaximumBytes"/>.</summary>
    public void Prune()
    {
        if (!Directory.Exists(directory)) return;
        var files = new DirectoryInfo(directory).GetFiles().OrderByDescending(x => x.LastWriteTimeUtc).ToList();
        var kept = 0L;
        foreach (var file in files)
        {
            if (kept + file.Length <= MaximumBytes && file.LastWriteTimeUtc > DateTime.UtcNow - Retention)
            {
                kept += file.Length;
                continue;
            }
            try { file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static readonly (string Extension, string MimeType)[] Types = [(".png", "image/png"), (".jpg", "image/jpeg")];
}
