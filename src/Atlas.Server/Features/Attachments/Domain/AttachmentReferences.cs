using System.Text.Json;
using System.Text.RegularExpressions;
using ISession = NHibernate.ISession;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Attachments.Domain;

/// <summary>Attachment references carried by a saved revision. Only canonical URL fields grant a reference.</summary>
public static partial class AttachmentReferences
{
    private const int MaximumReferences = 500;

    [GeneratedRegex("^/api/attachments/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/(content|download)$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalUrl();

    public static IReadOnlySet<Guid> Extract(string contentJson)
    {
        using var json = JsonDocument.Parse(contentJson, MarkdownDocumentValidator.JsonOptions);
        var result = new HashSet<Guid>();
        Visit(json.RootElement);
        return result;

        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in node.EnumerateObject())
                {
                    // Only URL fields can grant a reference; merely mentioning a URL in prose cannot.
                    if (property.Name is "src" or "url" or "href" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString()!;
                        var match = CanonicalUrl().Match(value);
                        if (match.Success)
                        {
                            result.Add(Guid.ParseExact(match.Groups[1].Value, "D"));
                        }
                        else if (value.StartsWith("/api/attachments/", StringComparison.Ordinal))
                        {
                            throw new ApiException(400, "Příloha má neplatný interní odkaz.");
                        }
                    }
                    Visit(property.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }

    /// <summary>
    /// The attachment references Markdown written over the API or MCP added to the page (absent from <paramref name="previousJson"/>):
    /// each an active attachment of this very page (404 <c>attachment_not_found</c>, which does not tell a foreign id from an unknown
    /// one), and an <c>image</c> node only of an attachment uploaded as an image (400 <c>attachment_not_image</c>). References the
    /// revision already held stay as they are; <see cref="BindRevisionAsync"/> checks every reference again.
    /// </summary>
    public static async Task RequireAddedAsync(ISession session, Guid pageId, string? previousJson, string contentJson, CancellationToken ct = default)
    {
        var (references, images) = (Extract(contentJson), Images(contentJson));
        var (oldReferences, oldImages) = previousJson is null ? (new HashSet<Guid>(), new HashSet<Guid>()) : (Extract(previousJson), Images(previousJson));
        var added = references.Except(oldReferences).Union(images.Except(oldImages)).ToArray();
        if (added.Length == 0)
        {
            return;
        }
        var attachments = (await session.QueryOver<Attachment>().WhereRestrictionOn(x => x.Id).IsIn(added).ListAsync(ct)).ToDictionary(x => x.Id);
        foreach (var id in added)
        {
            if (!attachments.TryGetValue(id, out var attachment) || attachment.PageId != pageId || attachment.ActiveTo is not null)
            {
                throw new ApiException(404, "Příloha {id} u této stránky není nebo už není dostupná. Nahrajte ji ke stránce.", "attachment_not_found", args: new Dictionary<string, object?> { ["id"] = id });
            }
            if (images.Contains(id) && !oldImages.Contains(id) && !attachment.IsImage)
            {
                throw new ApiException(400, $"Příloha {id:D} není obrázek (PNG, JPEG, GIF nebo WebP nahraný jako obrázek); odkažte na ni jako na soubor: " +
                    $"[název](/api/attachments/{id:D}/download).", "attachment_not_image");
            }
        }
    }

    /// <summary>The attachments <c>image</c> nodes show by their canonical <c>attrs.src</c>.</summary>
    private static HashSet<Guid> Images(string contentJson)
    {
        using var json = JsonDocument.Parse(contentJson, MarkdownDocumentValidator.JsonOptions);
        var result = new HashSet<Guid>();
        Visit(json.RootElement);
        return result;

        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    Visit(item);
                }
                return;
            }
            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "image"
                && node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("src", out var src)
                && src.ValueKind == JsonValueKind.String && CanonicalUrl().Match(src.GetString()!) is { Success: true } match && match.Groups[2].Value == "content")
            {
                result.Add(Guid.ParseExact(match.Groups[1].Value, "D"));
            }
            foreach (var property in node.EnumerateObject())
            {
                Visit(property.Value);
            }
        }
    }

    public static async Task BindRevisionAsync(ISession session, PageRevision revision, CancellationToken ct = default)
    {
        var ids = Extract(revision.ContentJson);
        if (ids.Count == 0)
        {
            return;
        }
        if (ids.Count > MaximumReferences)
        {
            throw new ApiException(400, "Stránka může odkazovat nejvýše na 500 příloh.");
        }
        var attachments = await session.QueryOver<Attachment>().WhereRestrictionOn(x => x.Id).IsIn(ids.ToArray()).ListAsync(ct);
        if (attachments.Count != ids.Count || attachments.Any(attachment => attachment.PageId != revision.PageId || attachment.ActiveTo is not null))
        {
            throw new ApiException(400, "Příloha nepatří k této stránce nebo už není dostupná.");
        }
        // The revision row must exist before its membership rows; NHibernate does not order these for us.
        await session.FlushAsync(ct);
        // Every caller holds the Page row FOR UPDATE, so "read existing, insert the rest"
        // replaces INSERT ... ON CONFLICT DO NOTHING without a race.
        var revisionId = revision.Id;
        var existing = await session.QueryOver<PageRevisionAttachment>()
            .Where(x => x.RevisionId == revisionId)
            .Select(x => x.AttachmentId)
            .ListAsync<Guid>(ct);
        foreach (var id in ids.Except(existing))
        {
            await session.SaveAsync(new PageRevisionAttachment { PageId = revision.PageId, RevisionId = revisionId, AttachmentId = id }, ct);
        }
        await session.FlushAsync(ct);
    }
}
