using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.Entities;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts;

/// <summary>
/// What a write learns about a contact (contacts spec §2): <c>contact_exists</c> (linked to the directory's), <c>contact_conflict</c>
/// (the directory's value holds, the written one is <see cref="New"/>), <c>possible_duplicate</c> (created, a weak match exists),
/// <c>contact_missing</c> (the referenced contact is trashed or gone, the snapshot stays). <see cref="Index"/> is the contact's place in
/// the batch (the document's or the changes' order).
/// </summary>
public sealed record ContactWarning(string Type, Guid ContactId, string Name)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Field { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonNode? Existing { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonNode? New { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<ContactUse>? UsedIn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Index { get; init; }

    /// <summary>The warning as one line for a model or a log.</summary>
    public string Message => Type switch
    {
        "contact_exists" => $"Kontakt {Name} už je v adresáři ({ContactId}){(UsedIn is { Count: > 0 } used ? ", používá se na: " + string.Join(", ", used.Take(5).Select(use => use.Title)) : "")}; vložen odkaz na existující kontakt.",
        "contact_conflict" => $"Kontakt {Name} ({ContactId}) má v adresáři jinou hodnotu pole {Field}; platí adresář. Změnu pole provede atlas_upsert_contact s contactId.",
        "possible_duplicate" => $"Kontakt {Name} se možná shoduje s existujícím ({ContactId}); založen nový. Pokud jde o stejnou osobu, sloučí je atlas_merge_contacts po potvrzení člověka.",
        _ => $"Kontakt {Name} ({ContactId}) je v koši nebo neexistuje; zůstává uložená kopie."
    };
}

public sealed record ContactLink(Guid? ContactId, bool Created, JsonObject Snapshot, IReadOnlyList<ContactWarning> Warnings);

public sealed record ContactsReport(IReadOnlyList<Guid> Used, IReadOnlyList<Guid> Created, IReadOnlyList<ContactWarning> Warnings)
{
    public static readonly ContactsReport Empty = new([], [], []);
}

public sealed record ResolvedContent(string ContentJson, bool Changed, ContactsReport Report);

/// <summary>
/// Links contacts to the one directory (contacts spec §2, §3): a snapshot with <c>objectId</c> is refreshed from its contact (merges
/// followed), one without is linked by identity to the contact a strong key finds, or a new wiki contact is created (K3: a weak match
/// only warns). Nothing of the directory is ever overwritten by a snapshot. Serves the page saves (<see cref="ResolveAsync"/>, the save's
/// report kept in <see cref="Last"/> for its caller), the web's picker and MCP's upsert (<see cref="LinkAsync"/>).
/// </summary>
public sealed class ContactLinker(ISession session, ContactDirectory directory, CreateObjectHandler create)
{
    /// <summary>The snapshot keys that belong to the page, never to the contact.</summary>
    private static readonly string[] PageKeys = ["kind", "internalUser", "username", "role"];

    /// <summary>The report of the last <see cref="ResolveAsync"/> in this scope: the save that ran it is the caller's.</summary>
    public ContactsReport Last { get; private set; } = ContactsReport.Empty;

    public async Task<ContactLink> LinkAsync(UserAccount actor, JsonObject snapshot, int? index, CancellationToken ct)
    {
        var name = Text(snapshot["name"]);
        if (Guid.TryParse(Text(snapshot["objectId"]), out var linked))
        {
            var live = (await directory.LiveAsync(actor, [linked], ct))[linked];
            return live.State switch
            {
                "ok" => new ContactLink(live.ContactId, false, Refreshed(snapshot, live.Contact!), []),
                "hidden" => new ContactLink(live.ContactId, false, WithId(snapshot, live.ContactId!.Value), []),
                _ => new ContactLink(live.ContactId ?? linked, false, snapshot, [new ContactWarning("contact_missing", live.ContactId ?? linked, name) { Index = index }])
            };
        }
        // An unresolved name or a template's place („PM“) is nobody yet: never a new directory contact (the page saves skip it, ResolveAsync).
        if (ContactSnapshots.IsUnresolved(snapshot))
        {
            throw new ApiException(400, $"Kontakt {name} ještě není vybraná osoba (unresolved): vyberte existující kontakt z adresáře, nebo pošlete kontakt bez unresolved.",
                "contact_unresolved", new Dictionary<string, object?> { ["index"] = index });
        }
        var fields = DirectoryFields(snapshot, index);
        await directory.LockAsync(ct);
        var match = await directory.MatchAsync(actor, snapshot, ct);
        if (match.Strong is { } strong)
        {
            var warnings = new List<ContactWarning> { new("contact_exists", strong.Id, strong.Name) { UsedIn = await directory.UsedInAsync(actor, strong.Id, ct), Index = index } };
            warnings.AddRange(match.Conflicts.Select(conflict => new ContactWarning("contact_conflict", strong.Id, strong.Name)
            {
                Field = conflict.Field, Existing = conflict.Existing, New = conflict.New, Index = index
            }));
            var live = (await directory.LiveAsync(actor, [strong.Id], ct))[strong.Id];
            return new ContactLink(strong.Id, false, live.State == "ok" ? Refreshed(snapshot, live.Contact!) : WithId(snapshot, strong.Id), warnings);
        }
        Guid id;
        try
        {
            id = (await create.Handle(new CreateObject(ContactObjectType.Id, ObjectScopes.Wiki, null, fields), ct)).Id;
        }
        catch (ApiException error) when (error.Status == 403)
        {
            throw new ApiException(403, $"Kontakt {name} není v adresáři a založit ho tam nesmíte. Vyberte existující kontakt z adresáře.", "contact_forbidden",
                new Dictionary<string, object?> { ["index"] = index });
        }
        // The next match of this transaction reads the new contact by SQL.
        await session.FlushAsync(ct);
        var created = ContactDirectory.Snapshot((await session.GetAsync<ContentResource>(id, ct))!);
        return new ContactLink(id, true, Refreshed(snapshot, created),
            match.Weak.Select(candidate => new ContactWarning("possible_duplicate", candidate.Id, candidate.Name) { Index = index }).ToArray());
    }

    /// <summary>
    /// Every contact of a stored rich document linked (<see cref="LinkAsync"/>), in document order; the same person written twice is
    /// linked once. A requirement heading's party contact (shape §6) follows its contact as a snapshot does: a merge's target id and the
    /// directory's name. The document is rewritten only when a snapshot changed.
    /// </summary>
    public async Task<ResolvedContent> ResolveAsync(UserAccount actor, string contentJson, CancellationToken ct)
    {
        Last = ContactsReport.Empty;
        if (!contentJson.Contains("\"contact", StringComparison.Ordinal) && !contentJson.Contains("\"partyContactId\"", StringComparison.Ordinal))
        {
            return new ResolvedContent(contentJson, false, Last);
        }
        var root = JsonNode.Parse(contentJson, documentOptions: new JsonDocumentOptions { MaxDepth = MarkdownDocumentValidator.JsonMaxDepth })!;
        var contacts = new List<JsonArray>();
        Collect(root["document"], contacts);
        var (used, created, warnings) = (new List<Guid>(), new List<Guid>(), new List<ContactWarning>());
        var links = new Dictionary<string, ContactLink>();
        var (index, changed) = (0, false);
        foreach (var list in contacts)
        {
            for (var i = 0; i < list.Count; i++, index++)
            {
                // An unresolved name (several people, nobody picked yet) waits for the person to pick: never linked or created here.
                if (list[i] is not JsonObject snapshot || Text(snapshot["name"]).Length == 0 || ContactSnapshots.IsUnresolved(snapshot))
                {
                    continue;
                }
                var key = Text(snapshot["objectId"]) is { Length: > 0 } objectId ? "id:" + objectId.ToLowerInvariant() : EntityIdentity.Of(snapshot);
                if (!links.TryGetValue(key, out var link))
                {
                    link = links[key] = await LinkAsync(actor, snapshot, index, ct);
                    warnings.AddRange(link.Warnings);
                    (link.Created ? created : used).Add(link.ContactId!.Value);
                }
                // The second occurrence of one person takes the first one's contact, keeping its own page keys.
                var next = link.Warnings.Any(warning => warning.Type == "contact_missing") ? snapshot : WithPageKeys(link.Snapshot, snapshot);
                if (!JsonNode.DeepEquals(next, snapshot))
                {
                    list[i] = next;
                    changed = true;
                }
            }
        }
        foreach (var attrs in Parties(root["document"]))
        {
            var party = ContactSnapshots.Party(attrs)!;
            var key = "id:" + Text(party["objectId"]).ToLowerInvariant();
            if (!links.TryGetValue(key, out var link))
            {
                link = links[key] = await LinkAsync(actor, party, index++, ct);
                warnings.AddRange(link.Warnings);
                used.Add(link.ContactId!.Value);
            }
            // The party's limit (shape §6).
            var name = Text(link.Snapshot["name"]) is { Length: > 200 } longer ? longer[..200] : Text(link.Snapshot["name"]);
            if (link.Warnings.All(warning => warning.Type != "contact_missing")
                && (link.ContactId!.Value.ToString() != Text(attrs["partyContactId"]) || name.Length > 0 && name != Text(attrs["party"])))
            {
                attrs["partyContactId"] = link.ContactId!.Value.ToString();
                attrs["party"] = name.Length > 0 ? name : Text(attrs["party"]);
                changed = true;
            }
        }
        Last = new ContactsReport(used.Distinct().ToArray(), created.Distinct().ToArray(), warnings);
        return new ResolvedContent(changed ? root.ToJsonString(RichDocument.JsonWrite) : contentJson, changed, Last);
    }

    /// <summary>The fields of a snapshot the directory stores, validated by its rules: a refusal names the contact, the field and the fix.
    /// A photo or website the directory cannot store (a relative one) stays out rather than refusing the contact.</summary>
    public static JsonObject DirectoryFields(JsonObject snapshot, int? index)
    {
        var name = Text(snapshot["name"]);
        var fields = new JsonObject();
        foreach (var field in ContactDirectory.Fields.Where(field => snapshot[field] is not null))
        {
            var value = snapshot[field]!.DeepClone();
            if (field is "photo" or "website" && !Valid(field, value))
            {
                continue;
            }
            fields[field] = value;
        }
        // An account's contact is known by the account (the web's accountContact), so its next mention finds it (user: = directory:).
        if (fields["directoryId"] is null && snapshot["internalUser"]?["id"] is JsonValue account && account.TryGetValue<string>(out var accountId))
        {
            fields["directoryId"] = accountId.Trim();
        }
        try
        {
            ContactFields.Create(fields);
            return fields;
        }
        catch (ObjectChangeRejected rejected)
        {
            var field = ContactDirectory.Fields.FirstOrDefault(field => fields[field] is { } value && !Valid(field, value.DeepClone())) ?? "name";
            throw new ApiException(400, $"Kontakt{(index is { } at ? $" č. {at + 1}" : "")} {name}: {rejected.Message} Opravte pole {field} " +
                "(e-mail ve tvaru jmeno@example.test, telefon z číslic, mezer a +, odkaz http(s)://…), nebo ho vynechte.", "invalid_contact",
                new Dictionary<string, object?> { ["index"] = index, ["field"] = field });
        }
    }

    /// <summary>A snapshot refreshed from its contact: the contact's fields and id, the page's own keys kept.</summary>
    public static JsonObject Refreshed(JsonObject snapshot, JsonObject contact)
    {
        var next = (JsonObject)contact.DeepClone();
        if (next["directoryId"] is null && snapshot["directoryId"] is { } directoryId)
        {
            next["directoryId"] = directoryId.DeepClone();
        }
        return WithPageKeys(next, snapshot);
    }

    private static JsonObject WithPageKeys(JsonObject contact, JsonObject snapshot)
    {
        var next = (JsonObject)contact.DeepClone();
        foreach (var key in PageKeys)
        {
            next.Remove(key);
            if (snapshot[key] is { } value)
            {
                next[key] = value.DeepClone();
            }
        }
        return next;
    }

    internal static JsonObject WithId(JsonObject snapshot, Guid id)
    {
        var next = (JsonObject)snapshot.DeepClone();
        next["objectId"] = id.ToString();
        return next;
    }

    private static bool Valid(string field, JsonNode value)
    {
        try
        {
            ContactFields.Create(field == "name" ? new JsonObject { ["name"] = value } : new JsonObject { ["name"] = "x", [field] = value });
            return true;
        }
        catch (ObjectChangeRejected)
        {
            return false;
        }
    }

    /// <summary>The contact lists of the plugin's nodes (ContactSnapshots' shape), nested ones included.</summary>
    internal static void Collect(JsonNode? node, List<JsonArray> found)
    {
        if (node is not JsonObject element)
        {
            return;
        }
        if (Text(element["type"]) is "contactCard" or "contactInline" && element["attrs"]?["data"]?["contacts"] is JsonArray contacts)
        {
            found.Add(contacts);
        }
        if (element["content"] is JsonArray children)
        {
            foreach (var child in children)
            {
                Collect(child, found);
            }
        }
    }

    /// <summary>The attributes of the requirement headings whose party is a contact (<see cref="ContactSnapshots.Party"/>).</summary>
    private static IEnumerable<JsonObject> Parties(JsonNode? document)
        => (document?["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(node => RichDocument.TypeOf(node) == "heading" && ContactSnapshots.Party(node["attrs"]) is not null).Select(node => (JsonObject)node["attrs"]!);

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
}
