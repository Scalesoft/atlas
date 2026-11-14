using Atlas.Server.Features.Attachments;

namespace Atlas.Server.Features.SpaceCovers.Domain;

/// <summary>What a card image may be: PNG, JPEG or WebP recognised by its signature, never by the declared type or name (an SVG
/// could carry script), up to 5 MiB as the web client limits it (spaceCover.ts).</summary>
public static class SpaceCoverImage
{
    public const long MaximumBytes = 5 * 1024 * 1024;

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.Ordinal)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp"
    };

    /// <summary>413 above the limit, 400 when the bytes are not a supported image; returns the detected type.</summary>
    public static string Validate(ReadOnlySpan<byte> content)
    {
        RequireSize(content.Length);
        return AttachmentFiles.TryDetectImage(content) is { } mime && Extensions.ContainsKey(mime)
            ? mime
            : throw new ApiException(400, "Obrázek prostoru musí být PNG, JPEG nebo WebP. Obsah souboru žádnému z nich neodpovídá.");
    }

    public static void RequireSize(long size)
    {
        if (size > MaximumBytes)
        {
            throw new ApiException(413, "Obrázek prostoru smí mít nejvýše 5 MB.");
        }
    }

    /// <summary>The file extension of a supported type (".png", ".jpg", ".webp"), null otherwise.</summary>
    public static string? Extension(string mimeType) => Extensions.GetValueOrDefault(mimeType);
}
