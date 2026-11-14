using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// The presentation of an embed of the page's own attachments, changed "on the side" of the Markdown (which carries only an image's
/// address, alternative text and title): an existing image or file card changed through fixed arguments (an image's alternative
/// text, width and alignment) or pointed at another attachment of its kind (a replacement is a new attachment; the old bytes stay
/// for the revisions that show them). The client sends an attachment id and never a URL, a node or an attribute: the node keeps
/// exactly the shape the editor (<c>@atlas/markdown</c>) writes. Nothing goes through Markdown, and heading ids and
/// plugin declarations stay as stored. Images and links are inserted and removed as Markdown (<see cref="MarkdownToRich"/>).
/// <para>
/// Before anything is returned, <see cref="RequireOnlyTouched"/> compares the document before and after: everything outside the
/// one touched node (every block, component, attribute, heading id, plugin declaration and envelope field) must be structurally
/// identical, apart from the empty paragraph the editor keeps after a closing block at the end of the page, and the touched node
/// must have the editor's shape (<see cref="IsEditorShape"/>). Otherwise 409 <c>embed_invariant_failed</c> and nothing is saved.
/// </para>
/// <para>
/// Refusals are <see cref="ApiException"/>s: 400 <c>invalid_argument</c> (alternative text, width, alignment, a look for a file
/// card, nothing to change), 400 <c>attachment_not_image</c>, 404 <c>embed_not_found</c>, 400 <c>embed_ambiguous</c> (embedded
/// more than once and no occurrence says which) and 409 <c>embed_invariant_failed</c>. Whether the replacement belongs to the page
/// and is active is the caller's check, in the transaction that saves.
/// </para>
/// </summary>
public static partial class RichMarkdownMerge
{
    public const int MaxImageAlt = 1_000;

    // Every plugin of the pinned editor at its version: the touched node is validated as the content of a document that may use any.
    private static readonly string EditorPlugins = JsonSerializer.Serialize(HostDocumentValidator.Instance.Schema.Plugins.Values.Select(x => new { id = x.Id, version = x.Version }));

    // ImagePresentation of features/images/presentation.ts in its key order, with its defaults: the editor stores the whole object
    // once any part of it was set.
    private static readonly (string Key, Func<JsonNode> Default)[] PresentationDefaults =
    [
        ("width", () => JsonValue.Create(100)), ("align", () => JsonValue.Create("center")), ("caption", () => JsonValue.Create("")),
        ("rotate", () => JsonValue.Create(0)), ("flip", () => JsonValue.Create(false)), ("brightness", () => JsonValue.Create(100)),
        ("contrast", () => JsonValue.Create(100)), ("saturation", () => JsonValue.Create(100)), ("filter", () => JsonValue.Create("none")),
        ("cropLeft", () => JsonValue.Create(0)), ("cropRight", () => JsonValue.Create(0)), ("cropTop", () => JsonValue.Create(0)),
        ("cropBottom", () => JsonValue.Create(0)), ("naturalWidth", () => JsonValue.Create(0)), ("naturalHeight", () => JsonValue.Create(0)),
    ];

    // No heading id is made and no page link resolved: an embed edit keeps the stored headings and adds no text.
    private static readonly MergeContext EmbedContext = new(_ => PageLinkResolution.None, _ => false,
        () => throw new InvalidOperationException("An embed edit keeps the stored heading ids."));

    [GeneratedRegex("^/api/attachments/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/(?<kind>content|download)$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalAttachmentUrl();

    /// <summary>The look of an image as the editor accepts it; a look for a file card is refused.</summary>
    public static void RequireLook(EmbedKind kind, ImageLook look)
    {
        if (kind == EmbedKind.File && !look.IsEmpty)
        {
            throw new ApiException(400, "Odkaz na soubor nemá alternativní text, šířku ani zarovnání.", "invalid_argument");
        }
        if (look.Alt is { } alt && (alt.Length > MaxImageAlt || alt.Any(char.IsControl)))
        {
            throw new ApiException(400, "Alternativní text obrázku je jeden řádek, nejvýše 1 000 znaků.", "invalid_argument");
        }
        if (look.Width is < 10 or > 100)
        {
            throw new ApiException(400, "Šířka obrázku je 10–100 % šířky stránky.", "invalid_argument");
        }
        if (look.Align is not (null or "left" or "center" or "right"))
        {
            throw new ApiException(400, "Zarovnání obrázku je \"left\", \"center\" nebo \"right\".", "invalid_argument");
        }
    }

    /// <summary>
    /// Changes the embed of <paramref name="attachmentId"/> (its <paramref name="occurrence"/>-th in document order when it is
    /// embedded more than once): an image's look, and for either kind the attachment it shows (<paramref name="replacement"/>, an
    /// image for an image). The rest of the node stays as stored; a replaced image keeps its look but forgets the size of the old
    /// picture, which the editor measures again; a replaced card keeps its description.
    /// </summary>
    public static MergeResult UpdateEmbed(RichDocument current, Guid attachmentId, int? occurrence, ImageLook look, EmbedAttachment? replacement)
    {
        if (look.IsEmpty && replacement is null)
        {
            throw new ApiException(400, "Zadejte, co se má změnit: alternativní text, šířku, zarovnání nebo jinou přílohu.", "invalid_argument");
        }
        var root = WorkingCopy(current);
        var (node, kind, path) = Locate(root, attachmentId, occurrence);
        RequireLook(kind, look);
        if (replacement is not null)
        {
            RequireKind(kind, replacement);
        }
        var replaced = replacement is not null && replacement.Id != attachmentId;
        if (kind == EmbedKind.Image)
        {
            var attrs = node["attrs"]!.AsObject();
            if (replaced)
            {
                attrs["src"] = ContentUrl(replacement!.Id);
            }
            if (look.Alt is not null)
            {
                attrs["alt"] = look.Alt;
            }
            if (look.Width is not null || look.Align is not null || (replaced && attrs["presentation"] is JsonObject))
            {
                attrs["presentation"] = Presentation(attrs["presentation"], look, replaced);
            }
        }
        else if (replaced)
        {
            node["attrs"] = new JsonObject { ["data"] = FileData(replacement!, node["attrs"]?["data"]?["description"]) };
        }
        return Write(current, root, kind, path);
    }

    /// <summary>
    /// The embeds within <paramref name="node"/> (itself included) in document order: the attachment each shows and its kind. Only
    /// canonical URLs count, as for the revision's attachment references; a link in text is no embed.
    /// </summary>
    public static IReadOnlyList<(Guid AttachmentId, EmbedKind Kind)> EmbedsIn(JsonNode? node)
    {
        var found = new List<(Guid, EmbedKind)>();
        Walk(node, x =>
        {
            if (EmbedOf(x) is { } embed)
            {
                found.Add(embed);
            }
        });
        return found;
    }

    /// <summary>
    /// The touched node is one the editor itself could have written: a canonical URL of the right kind (Atlas's rule for an embed
    /// of the page's own attachment), and otherwise exactly what the pinned editor accepts (Atlas.Markdown.Sync's validator on
    /// the node alone in a document): only attributes of its schema, an image presentation <c>imagePresentation</c> accepts, card
    /// data <c>normalizeAttachment</c> accepts. Everything is checked on the node as it will be stored.
    /// </summary>
    private static bool IsEditorShape(JsonObject node, EmbedKind kind)
    {
        if (node.Any(x => x.Key is not ("type" or "attrs")) || node["attrs"] is not JsonObject attrs)
        {
            return false;
        }
        var canonical = kind == EmbedKind.Image
            ? RichDocument.TypeOf(node) == "image" && Url(attrs["src"]) is "content"
            : RichDocument.TypeOf(node) == "attachment" && Url(attrs["data"]?["url"]) is not null;
        return canonical && HostDocumentValidator.Instance.Validate(
            $$$"""{"format":"{{{MarkdownDocumentValidator.Format}}}","version":1,"plugins":{{{EditorPlugins}}},"document":{"type":"doc","content":[{{{node.ToJsonString()}}}]}}""") is null;
    }

    // ---- the working copy and the invariant ----

    private static JsonArray WorkingCopy(RichDocument current)
    {
        return new JsonArray(current.Blocks.Select(x => (JsonNode)Clone(x)).ToArray());
    }

    /// <summary>
    /// Writes the working copy as the new revision, after checking the touched node's shape and that nothing else changed;
    /// <paramref name="path"/> is the touched node's path, the same in the stored and the new blocks.
    /// </summary>
    private static MergeResult Write(RichDocument current, JsonArray root, EmbedKind kind, int[] path)
    {
        var envelope = (JsonObject)current.Envelope.DeepClone();
        envelope.Remove("document");
        // Checked as it will be stored: parsed back from its JSON, so a number is a number whatever .NET type built it.
        if (!IsEditorShape(JsonNode.Parse(NodeAt(root, path).ToJsonString())!.AsObject(), kind))
        {
            throw Unsafe();
        }
        var blocks = root.OfType<JsonObject>().ToList();
        root.Clear();
        var result = Finish(envelope, blocks, [], EmbedContext, keepStored: true);
        RequireOnlyTouched(current, result, path);
        return result;
    }

    /// <summary>
    /// The hard invariant of an embed edit: with the touched node replaced by one marker on both sides, the stored document and the
    /// new one are structurally identical (the trailing paragraph <see cref="Finish"/> keeps aside), and so are the envelopes.
    /// </summary>
    private static void RequireOnlyTouched(RichDocument current, MergeResult result, int[] path)
    {
        var stored = WorkingCopy(current);
        var written = JsonNode.Parse(result.ContentJson)!.AsObject();
        var blocks = written["document"]?["content"]?.DeepClone() as JsonArray ?? throw Unsafe();
        Mark(stored, path);
        Mark(blocks, path);
        if (blocks.Count == stored.Count + 1 && JsonNode.DeepEquals(blocks[^1], new JsonObject { ["type"] = "paragraph" })
            && (stored.Count == 0 || RichDocument.TypeOf(stored[^1]) != "paragraph"))
        {
            blocks.RemoveAt(blocks.Count - 1);
        }
        if (!JsonNode.DeepEquals(stored, blocks))
        {
            throw Unsafe();
        }
        var envelope = (JsonObject)current.Envelope.DeepClone();
        envelope.Remove("document");
        written.Remove("document");
        if (!JsonNode.DeepEquals(envelope, written))
        {
            throw Unsafe();
        }
    }

    private static void Mark(JsonArray root, int[] path)
    {
        ParentOf(root, path)[path[^1]] = new JsonObject { ["type"] = "\u0000embed" };
    }

    private static ApiException Unsafe()
    {
        return new ApiException(409, "Úpravu nešlo provést beze změny ostatního obsahu stránky; nic se neuložilo.", "embed_invariant_failed");
    }

    // ---- finding embeds ----

    /// <summary>The embed to change in the working copy, its kind and its path (indices through the content arrays).</summary>
    private static (JsonObject Node, EmbedKind Kind, int[] Path) Locate(JsonArray root, Guid attachmentId, int? occurrence)
    {
        if (occurrence is < 1)
        {
            throw new ApiException(400, "occurrence je pořadí vložení přílohy na stránce od 1.", "invalid_argument");
        }
        var matches = new List<(JsonObject, EmbedKind, int[])>();
        Collect(root, []);
        if (matches.Count == 0 || occurrence > matches.Count)
        {
            throw new ApiException(404, matches.Count == 0 ? "Tato příloha není na stránce vložená." : $"Příloha je na stránce vložená jen {matches.Count}×.",
                "embed_not_found");
        }
        if (occurrence is null && matches.Count > 1)
        {
            throw new ApiException(400, $"Příloha je na stránce vložená {matches.Count}×; zadejte occurrence 1–{matches.Count} (pořadí v dokumentu).",
                "embed_ambiguous", new Dictionary<string, object?> { ["occurrences"] = matches.Count });
        }
        return matches[(occurrence ?? 1) - 1];

        void Collect(JsonArray content, int[] path)
        {
            for (var index = 0; index < content.Count; index++)
            {
                if (content[index] is not JsonObject child)
                {
                    continue;
                }
                int[] here = [.. path, index];
                if (EmbedOf(child) is { } embed && embed.AttachmentId == attachmentId)
                {
                    matches.Add((child, embed.Kind, here));
                }
                if (child["content"] is JsonArray inner)
                {
                    Collect(inner, here);
                }
            }
        }
    }

    private static JsonArray ParentOf(JsonArray root, int[] path)
    {
        var parent = root;
        foreach (var index in path[..^1])
        {
            parent = (JsonArray)parent[index]!["content"]!;
        }
        return parent;
    }

    private static JsonObject NodeAt(JsonArray root, int[] path)
    {
        return (JsonObject)ParentOf(root, path)[path[^1]]!;
    }

    private static (Guid AttachmentId, EmbedKind Kind)? EmbedOf(JsonObject node)
    {
        var (kind, url) = RichDocument.TypeOf(node) switch
        {
            "image" => (EmbedKind.Image, node["attrs"]?["src"]),
            "attachment" => (EmbedKind.File, node["attrs"]?["data"]?["url"]),
            _ => (EmbedKind.Image, null),
        };
        return Text(url) is { } text && CanonicalAttachmentUrl().Match(text) is { Success: true } match
            ? (Guid.ParseExact(match.Groups["id"].Value, "D"), kind)
            : null;
    }

    /// <summary>The kind of a canonical attachment URL (<c>content</c> or <c>download</c>), null for anything else.</summary>
    private static string? Url(JsonNode? value)
    {
        return Text(value) is { } text && CanonicalAttachmentUrl().Match(text) is { Success: true } match ? match.Groups["kind"].Value : null;
    }

    private static string? Text(JsonNode? value)
    {
        return value is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;
    }

    // ---- the nodes ----

    private static void RequireKind(EmbedKind kind, EmbedAttachment attachment)
    {
        if (kind == EmbedKind.Image && !attachment.IsImage)
        {
            throw new ApiException(400, "Příloha není obrázek (PNG, JPEG, GIF nebo WebP nahraný jako obrázek); vložte ji jako odkaz na soubor.",
                "attachment_not_image");
        }
    }

    private static JsonObject FileData(EmbedAttachment attachment, JsonNode? description)
    {
        var data = new JsonObject
        {
            ["version"] = 1,
            ["name"] = attachment.Name.Trim(),
            ["url"] = "/api/attachments/" + attachment.Id.ToString("D") + "/download",
            ["size"] = attachment.Size,
            ["mimeType"] = attachment.MimeType,
        };
        if (description is not null)
        {
            data["description"] = description.DeepClone();
        }
        return data;
    }

    private static string ContentUrl(Guid id)
    {
        return "/api/attachments/" + id.ToString("D") + "/content";
    }

    /// <summary>The whole presentation object: the stored values (defaults where absent), the new width and alignment.</summary>
    private static JsonObject Presentation(JsonNode? stored, ImageLook look, bool replaced)
    {
        // Rebuilding keeps only the known keys: a stored key outside them would be lost silently, so the update is refused instead.
        if (stored is JsonObject keys && keys.Any(x => PresentationDefaults.All(y => y.Key != x.Key)))
        {
            throw Unsafe();
        }
        var presentation = new JsonObject();
        foreach (var (key, fallback) in PresentationDefaults)
        {
            presentation[key] = stored?[key]?.DeepClone() ?? fallback();
        }
        if (look.Width is { } width)
        {
            presentation["width"] = width;
        }
        if (look.Align is { } align)
        {
            presentation["align"] = align;
        }
        if (replaced)
        {
            // Zero is the editor's "not measured yet"; the old picture's size would distort the new one's crop and aspect ratio.
            presentation["naturalWidth"] = 0;
            presentation["naturalHeight"] = 0;
        }
        return presentation;
    }
}
