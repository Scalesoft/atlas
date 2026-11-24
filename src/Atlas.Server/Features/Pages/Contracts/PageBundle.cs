using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PersonalLibrary.Contracts;

namespace Atlas.Server.Features.Pages.Contracts;

/// <summary>
/// What the page view asks for right after the page (<c>GET /api/pages/{id}?bundle=true</c>), in one response, read in the page's own
/// transaction under its locks (GetPageWithBundleHandler). Every part keeps the check of its own endpoint; a part the reader may not
/// see, or one that fails, stays null and the client asks for it alone.
/// </summary>
public sealed record PageBundle(PersonalPageState? PersonalState, PageTagState? Tags, PageConnections? Connections,
    LiveContactsResult? Contacts, PageReferenceList? Pages, PageBundleCollaboration? Collaboration)
{
    /// <summary>5.2: the page's verification (the chip at the title, the panel's facts); state off while verification is off for the page.</summary>
    public PageVerifications.PageVerificationView? Verification { get; init; }

    /// <summary>5.2 „Čtenost“ of the panel: distinct readers in the last 90 days, as Čtenost stránky counts them; null while verification is
    /// off for the page.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? Readers { get; init; }

    /// <summary>The directory contacts and linked pages a document names (chips, links), as the client would ask for them.</summary>
    public static (List<Guid> Contacts, HashSet<Guid> Pages) ReferencedIds(string contentJson)
    {
        var contacts = new HashSet<Guid>();
        var pages = new HashSet<Guid>();
        try
        {
            // JsonDocument, not JsonNode: a read-only walk of the largest pages (~1 MB) without building a mutable tree; the editor's depth.
            using var document = JsonDocument.Parse(contentJson, MarkdownDocumentValidator.JsonOptions);
            Walk(document.RootElement, contacts, pages);
        }
        catch (JsonException)
        {
            // An unreadable body has no references to bundle; the page itself is served as before.
        }
        return (contacts.ToList(), pages);
    }

    private static void Walk(JsonElement node, HashSet<Guid> contacts, HashSet<Guid> pages)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                PageLinkTargets.Add(node, pages);
                if (node.TryGetProperty("contacts", out var people) && people.ValueKind == JsonValueKind.Array)
                {
                    foreach (var person in people.EnumerateArray())
                    {
                        if (Guid.TryParse(StringOf(person, "objectId"), out var contact))
                        {
                            contacts.Add(contact);
                        }
                    }
                }
                foreach (var child in node.EnumerateObject())
                {
                    Walk(child.Value, contacts, pages);
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in node.EnumerateArray())
                {
                    Walk(child, contacts, pages);
                }
                break;
        }
    }

    private static string? StringOf(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>The page's live session as the hub would serve it, so the editor is built at once and the hub only syncs what came after: the
/// session of <c>POST …/collaboration</c> (writer) or <c>GET …/collaboration</c> (reader of a live page). Its Yjs state is a read of its
/// own (<c>GET …/collaboration/state</c>), which the browser keeps and revalidates by ETag, so a warm open does not download it again.</summary>
public sealed record PageBundleCollaboration(string DocumentId, Guid Generation, int CurrentVersion, bool ReadOnly);
