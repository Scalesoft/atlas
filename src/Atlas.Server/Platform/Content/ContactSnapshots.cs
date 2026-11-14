using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// The one C# reader of the contacts plugin's snapshots in stored content (Atlas 3 plan §14.2): <c>contactCard</c> and
/// <c>contactInline</c> nodes keep their people in <c>attrs.data.contacts</c>, a requirement heading its party's contact in
/// <c>attrs.party</c> / <c>attrs.partyContactId</c> (shape §6, <see cref="Party"/>). Search shows them as widgets (SearchWidgets) and the
/// entity catalog stores them as occurrences (EntityCatalog), both through this.
/// </summary>
public static class ContactSnapshots
{
    /// <summary>The fields of plugins/contacts' MarkdownContact; anything else in stored data the editor drops, so the page never shows it.</summary>
    public static readonly string[] Fields =
        ["name", "organization", "department", "title", "username", "photo", "website", "address", "phones", "emails", "directoryId", "objectId", "kind", "internalUser", "unresolved"];

    /// <summary>
    /// A snapshot of a name several people have, nobody picked yet (plugins/contacts <c>unresolved</c>): no person, so never linked, listed
    /// or matched. One with an <c>objectId</c> is that contact whatever the flag says; both as the editor reads them (data.ts).
    /// </summary>
    public static bool IsUnresolved(JsonObject snapshot) =>
        snapshot["unresolved"] is JsonValue flag && (flag.GetValueKind() == JsonValueKind.True || flag.GetValueKind() == JsonValueKind.String && flag.GetValue<string>() == "true")
        && !(snapshot["objectId"] is JsonValue id && id.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetValue<string>()));

    /// <summary>The contacts of one node with a name, none unresolved (<see cref="IsUnresolved"/>); none for a node of another type.</summary>
    public static IEnumerable<JsonElement> Of(string? type, JsonElement attrs) =>
        Named(type, attrs).Where(contact => !IsUnresolved(JsonObject.Create(contact)!));

    /// <summary>The contacts of one node with a name, unresolved ones too: the names a reader sees in the page.</summary>
    public static IEnumerable<JsonElement> Named(string? type, JsonElement attrs)
    {
        if (type == "heading")
        {
            if (attrs.ValueKind == JsonValueKind.Object && Party(JsonObject.Create(attrs)) is { } party)
            {
                yield return JsonSerializer.SerializeToElement(party);
            }
            yield break;
        }
        if (type is not ("contactCard" or "contactInline") || attrs.ValueKind != JsonValueKind.Object
            || !attrs.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("contacts", out var contacts) || contacts.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var contact in contacts.EnumerateArray())
        {
            if (contact.ValueKind == JsonValueKind.Object && contact.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                yield return contact;
            }
        }
    }

    /// <summary>A requirement heading's party as a contact snapshot, <c>{name, objectId}</c> from its attributes <paramref name="attrs"/>
    /// (<c>party</c>, <c>partyContactId</c>), when it names a contact; else null.</summary>
    public static JsonObject? Party(JsonNode? attrs)
        => attrs?["party"] is JsonValue party && party.TryGetValue<string>(out var name) && name.Trim().Length > 0
            && attrs["partyContactId"] is JsonValue id && id.TryGetValue<string>(out var objectId) && objectId.Trim().Length > 0
            ? new JsonObject { ["name"] = name.Trim(), ["objectId"] = objectId.Trim() } : null;

    /// <summary>The contacts of a parsed document's nodes as they are, for a save that rewrites them in place.</summary>
    public static IEnumerable<JsonObject> In(JsonNode? node)
    {
        if (node is not JsonObject element)
        {
            yield break;
        }
        if (element["type"] is JsonValue type && type.GetValueKind() == JsonValueKind.String && type.GetValue<string>() is "contactCard" or "contactInline"
            && element["attrs"] is JsonObject attrs && attrs["data"] is JsonObject data && data["contacts"] is JsonArray contacts)
        {
            foreach (var contact in contacts.OfType<JsonObject>())
            {
                yield return contact;
            }
        }
        if (element["content"] is JsonArray content)
        {
            foreach (var contact in content.SelectMany(In))
            {
                yield return contact;
            }
        }
    }

    /// <summary>The plugin's fields of a contact, each once.</summary>
    public static JsonObject FieldsOf(JsonElement contact) => new(contact.EnumerateObject()
        .Where(field => Fields.Contains(field.Name, StringComparer.Ordinal))
        .DistinctBy(field => field.Name, StringComparer.Ordinal)
        .Select(field => KeyValuePair.Create(field.Name, JsonNode.Parse(field.Value.GetRawText()))));

    /// <summary>
    /// Every contact of a stored rich document (<c>{"document": …}</c>) in document order, nested blocks and inline mentions included,
    /// with the id of the heading above it (the anchor a link opens).
    /// </summary>
    public static IReadOnlyList<(JsonObject Contact, string? HeadingId)> Read(string contentJson)
    {
        using var document = JsonDocument.Parse(contentJson, MarkdownDocumentValidator.JsonOptions);
        var found = new List<(JsonObject, string?)>();
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("document", out var root))
        {
            string? heading = null;
            Walk(root, ref heading, found);
        }
        return found;
    }

    private static void Walk(JsonElement node, ref string? heading, List<(JsonObject, string?)> found)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            if (type == "heading" && attrs.TryGetProperty("headingId", out var id) && id.ValueKind == JsonValueKind.String)
            {
                heading = id.GetString();
            }
            foreach (var contact in Of(type, attrs))
            {
                found.Add((FieldsOf(contact), heading));
            }
        }
        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
            {
                Walk(child, ref heading, found);
            }
        }
    }
}
