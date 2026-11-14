using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Commands;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Features.Projector.Persistence;

namespace Atlas.Server.Features.Projector.Commands;

/// <summary>What a block command links outside its block (plan 3.1 M4b): the party's contact, through the contacts' identity service as
/// atlas_upsert_contact, and dependencies on other pages' blocks, which must not close a cycle.</summary>
public sealed class RequirementLinks(ResolveContactHandler contacts, CrossPageRequirements cross)
{
    /// <summary>The change with the party's contact resolved: <see cref="RequirementChange.PartyContactId"/> a contact of the directory
    /// (one the actor may read), <see cref="RequirementChange.PartyContact"/> the one its identity finds or a new one; the party then
    /// reads its name unless the change gives one. The contacts' warnings (a possible duplicate) come back as text.</summary>
    public async Task<(RequirementChange Change, IReadOnlyList<string> Warnings)> ContactAsync(RequirementChange change, CancellationToken ct)
    {
        Guid? id = null;
        if (change.PartyContactId is { Length: > 0 } text)
        {
            id = Guid.TryParse(text.Trim(), out var parsed)
                ? parsed
                : throw RequirementAttributes.InvalidArgument("partyContactId", "partyContactId: id kontaktu z adresáře (GUID), nebo prázdné pro odpojení kontaktu.");
        }
        if (id is null && change.PartyContact is null)
        {
            return (change with { PartyContact = null }, []);
        }
        var resolved = await contacts.Handle(new ResolveContact(change.PartyContact ?? new JsonObject(), id), ct);
        if (resolved.ContactId is not { } contact || resolved.Warnings.Any(x => x.Type == "contact_missing"))
        {
            throw RequirementAttributes.InvalidArgument("partyContactId",
                "partyContactId: takový kontakt v adresáři není; vyberte kontakt z adresáře, nebo stranu zapište textem (party).");
        }
        var name = resolved.Contact["name"] is JsonValue value && value.TryGetValue<string>(out var named) ? named.Trim() : "";
        var warnings = resolved.Warnings.Where(x => x.Type != "contact_exists")
            .Select(x => x.Type == "possible_duplicate" ? $"Kontakt „{name}“ byl založen; v adresáři může být tatáž osoba („{x.Name}“, id {x.ContactId})." : $"Kontakt „{name}“: {x.Type}.")
            .ToArray();
        return (change with
        {
            PartyContactId = contact.ToString("D"), Party = change.Party ?? (name.Length > 0 ? name[..Math.Min(name.Length, RequirementAttributes.MaxText)] : null),
            PartyContact = null
        }, warnings);
    }

    /// <summary>400 cyclic_dependency with the path when the block's dependencies lead back to it through another page.</summary>
    public async Task RefuseCycleAsync(RequirementDraft draft, string blockId, CancellationToken ct)
    {
        var block = RequirementTree.Find(draft.Rich, blockId, draft.Page.Page.Id);
        var local = RequirementTree.Blocks(draft.Rich, draft.Page.Page.Id).GroupBy(RequirementTree.Key, StringComparer.Ordinal).Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => RequirementAttributes.ParseRequires(RequirementAttributes.Text(group.First()["attrs"], "requires")),
                StringComparer.Ordinal);
        var key = RequirementTree.Key(block);
        if (await cross.CycleAsync(draft.Page.Actor, draft.Page.Page.Id, key, local.GetValueOrDefault(key, []), local, ct) is { } path)
        {
            throw Cycle(path);
        }
    }

    public static ApiException Cycle(IReadOnlyList<string> path)
        => new(400, $"Závislosti požadavků tvoří kruh přes jiné stránky: {string.Join(" → ", path)}.", "cyclic_dependency",
            new Dictionary<string, object?> { ["path"] = path });
}
