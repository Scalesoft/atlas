using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Attachments;

namespace Atlas.Server.Features.Contacts.Domain;

/// <summary>
/// The small picture of a contact (Atlas 3.5 package G): a company's logo or a person's photo a user uploads, shown in the contact's
/// chip before the name and on its card. One per contact, removed with the contact's envelope; the contact's <c>photo</c> field names
/// it (<see cref="ContactPhotos.Url"/>).
/// </summary>
public class ContactPhoto
{
    public virtual Guid ContactId { get; set; }
    public virtual string MimeType { get; set; } = "";
    public virtual string Sha256 { get; set; } = "";
    public virtual byte[] Content { get; set; } = [];
}

/// <summary>
/// The rules of <see cref="ContactPhoto"/>: PNG, JPEG or WebP recognised by its signature (never SVG, which could carry script), at
/// most <see cref="MaximumBytes"/>; the web makes it a 64 px square before the upload, so the chip's one image stays tiny. The URL's
/// <c>v</c> changes with the bytes, so a browser keeps a picture for good.
/// </summary>
public static partial class ContactPhotos
{
    public const int MaximumBytes = 64 * 1024;

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/webp" };

    /// <summary>The detected type and the SHA-256 (hex) of an upload; 413 above the limit, 400 for anything but PNG, JPEG or WebP.</summary>
    public static (string MimeType, string Sha256) Validate(ReadOnlySpan<byte> content)
    {
        RequireSize(content.Length);
        if (content.Length == 0 || AttachmentFiles.TryDetectImage(content) is not { } mimeType || !Types.Contains(mimeType))
        {
            throw new ApiException(400, "Logo nebo fotka kontaktu musí být PNG, JPEG nebo WebP. Obsah souboru žádnému z nich neodpovídá.", "invalid_photo");
        }
        return (mimeType, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    /// <summary>413 above the limit, before anything is read.</summary>
    public static void RequireSize(long size)
    {
        if (size > MaximumBytes)
        {
            throw new ApiException(413, "Logo nebo fotka kontaktu smí mít nejvýše 64 KB. Zmenšete obrázek, v čipu stačí 64 × 64 px.", "photo_too_large");
        }
    }

    /// <summary>Where the web reads the picture: the contact and the first 16 hex digits of the content's hash.</summary>
    public static string Url(Guid contactId, string sha256) => $"/api/contacts/{contactId:D}/photo?v={Version(sha256)}";

    /// <summary>The <c>v</c> of <see cref="Url"/>: what a request naming the current picture carries.</summary>
    public static string Version(string sha256) => sha256[..16];

    /// <summary>Whether a photo field names an uploaded picture (<see cref="Url"/>), the one relative address a contact may hold.</summary>
    public static bool IsUrl(string url) => Own().IsMatch(url);

    /// <summary>Whether a photo field names <paramref name="contactId"/>'s own uploaded picture, never another contact's.</summary>
    public static bool IsUrl(string url, Guid contactId) => IsUrl(url) && url.StartsWith($"/api/contacts/{contactId:D}/", StringComparison.Ordinal);

    [GeneratedRegex(@"^/api/contacts/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/photo\?v=[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex Own();
}
