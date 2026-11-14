using System.Text.Json.Serialization;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// What a page embeds of its own attachments: an inline <see cref="Image"/> (the editor's <c>image</c> node, <c>attrs.src</c> the
/// canonical <c>/api/attachments/{id}/content</c>) or a <see cref="File"/> card (the attachments plugin's <c>attachment</c> node,
/// <c>attrs.data.url</c> the canonical <c>/api/attachments/{id}/download</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EmbedKind>))]
public enum EmbedKind
{
    [JsonStringEnumMemberName("image")]
    Image,
    [JsonStringEnumMemberName("file")]
    File,
}

/// <summary>The attachment an embed points at, as the page's own attachment row describes it (never client input).</summary>
public sealed record EmbedAttachment(Guid Id, string Name, long Size, string MimeType, bool IsImage);

/// <summary>
/// How an image embed looks, each part optional: the alternative text and, in the editor's <c>attrs.presentation</c>, the width
/// in percent of the column (10–100) and the alignment (<c>left</c>, <c>center</c>, <c>right</c>). Null keeps what is stored.
/// </summary>
public sealed record ImageLook(string? Alt = null, int? Width = null, string? Align = null)
{
    public bool IsEmpty => Alt is null && Width is null && Align is null;
}
