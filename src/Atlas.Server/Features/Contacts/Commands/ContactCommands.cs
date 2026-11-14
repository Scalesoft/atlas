using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Contacts.Queries;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.Entities;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts.Commands;

// The contact directory as one entity (contacts spec 2026-10-30 §2, §4, §5, §7): the web's picker, live references, usage, the
// duplicates page and the merge; MCP's upsert and merge run the same handlers. Their reads and results are in Contracts (the command coverage counts every record here).

/// <summary>Links a snapshot to the directory (<see cref="ContactLinker.LinkAsync"/>); with <see cref="ContactId"/> that contact.</summary>
public sealed record ResolveContact(JsonObject? Contact, Guid? ContactId = null);

/// <summary>Merges <see cref="SourceId"/> into <see cref="TargetId"/>; MCP only after a person confirmed it.</summary>
public sealed record MergeContacts(Guid SourceId, Guid TargetId);

public sealed class ContactReadHandler(IContentAccess access, ContactDirectory directory)
{
    public async Task<ContactMatchResult> Handle(MatchContact command, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        var contact = command.Contact ?? throw ContactRequests.Missing();
        var match = await directory.MatchAsync(actor, contact, ct);
        var live = await directory.LiveAsync(actor, match.Weak.Select(x => x.Id).Append(match.Strong?.Id ?? Guid.Empty).Where(id => id != Guid.Empty).ToArray(), ct);
        ContactRef Ref(ContactCandidate candidate, IReadOnlyList<ContactUse>? used = null)
            => new(candidate.Id, candidate.Name, live.TryGetValue(candidate.Id, out var found) ? found.Contact : null, used);
        return new ContactMatchResult(match.Strong is { } strong ? Ref(strong, await directory.UsedInAsync(actor, strong.Id, ct)) : null,
            match.Weak.Select(candidate => Ref(candidate)).ToArray(), match.Conflicts);
    }

    public async Task<LiveContactsResult> Handle(LiveContacts command, CancellationToken ct)
    {
        if (command.Ids is not { Count: > 0 and <= 200 } ids)
        {
            throw new ApiException(400, "Uveďte 1 až 200 id kontaktů.", "invalid_argument");
        }
        return new LiveContactsResult(await directory.LiveAsync(await access.LockActorAsync(null, ct), ids, ct));
    }
}

public sealed class ResolveContactHandler(IContentAccess access, ContactLinker linker)
{
    public async Task<ResolvedContact> Handle(ResolveContact command, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        var contact = (JsonObject)(command.Contact ?? throw ContactRequests.Missing()).DeepClone();
        if (command.ContactId is { } id)
        {
            contact["objectId"] = id.ToString();
        }
        var link = await linker.LinkAsync(actor, contact, null, ct);
        return new ResolvedContact(link.ContactId, link.Created, link.Snapshot, link.Warnings);
    }
}

public sealed class ContactDuplicatesHandler(ISession session, IContentAccess access, RelationNodes nodes)
{
    public const int MaxPairs = 200;

    /// <summary>
    /// Pairs of live wiki and space contacts the actor may read that share a weak key (a phone, the name with the organization) or the
    /// name alone: the strong keys are refused on write (K1), so what is left to a person is what may be one person.
    /// </summary>
    // shortcut: every contact is read and keyed in memory; move the keys into a table when the directory has thousands of people.
    public async Task<ContactDuplicateList> Handle(ListContactDuplicates query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        var rows = await session.CreateSQLQuery("""
                SELECT r."Id", r."Title", CAST(r."Data" AS text) FROM "ContentResource" r
                WHERE r."TypeId" = :type AND r."TrashedAt" IS NULL AND r."Scope" <> 'private' AND r."Data" IS NOT NULL
                ORDER BY r."CreatedAt", r."Id"
                """)
            .SetParameter("type", ContactObjectType.Id).ListAsync<object[]>(ct);
        var contacts = rows.Select(row => (Id: (Guid)row[0], Name: (string?)row[1] ?? "", Data: JsonNode.Parse((string)row[2])!.AsObject())).ToArray();
        var (readable, _) = await nodes.ReadAsync(actor, null, contacts.Select(x => x.Id), [ContactObjectType.Id], ct);
        contacts = contacts.Where(x => readable.TryGetValue(x.Id, out var node) && node.Visible).ToArray();
        var pairs = new Dictionary<(Guid, Guid), ContactDuplicate>();
        foreach (var group in contacts
                     .SelectMany(x => EntityIdentity.Keys(x.Data).Where(key => !key.Strong).Select(key => key.Value)
                         .Append("same-name:" + EntityIdentity.Normalize(x.Name)).Select(key => (Key: key, Contact: x)))
                     .GroupBy(x => x.Key).Where(group => group.Count() > 1))
        {
            var members = group.Select(x => x.Contact).ToArray();
            for (var i = 0; i < members.Length && pairs.Count < MaxPairs; i++)
            {
                for (var j = i + 1; j < members.Length && pairs.Count < MaxPairs; j++)
                {
                    var reason = group.Key.StartsWith("phone:", StringComparison.Ordinal) ? "phone" : group.Key.StartsWith("name:", StringComparison.Ordinal) ? "name" : "similar";
                    pairs.TryAdd((members[i].Id, members[j].Id), new ContactDuplicate(
                        new ContactRef(members[i].Id, members[i].Name, members[i].Data), new ContactRef(members[j].Id, members[j].Name, members[j].Data), reason));
                }
            }
        }
        return new ContactDuplicateList(pairs.Values.ToArray());
    }
}

/// <summary>
/// The merge (contacts spec §5): the target takes the fields it lacks and the source's addresses and phones, the source goes to the object
/// trash with <c>mergedInto</c> in its data, which every read and relation follows, the boards' cards (live, as a board batch) and the tree's
/// reference nodes of the source show the target. Pages keep their revisions; the next save of a page writes the target's id. Who may change both contacts may merge them.
/// </summary>
public sealed class MergeContactsHandler(ISession session, IContentAccess access, ContactDirectory directory, ContactObjectType contacts,
    TrashObjectHandler trash, ApplyObjectChangeHandler change, BoardBatchWriter boardWriter, Outbox outbox)
{
    public async Task<MergedContacts> Handle(MergeContacts command, CancellationToken ct)
    {
        if (command.SourceId == command.TargetId)
        {
            throw new ApiException(400, "Kontakt nelze sloučit sám se sebou.", "invalid_argument");
        }
        // The edit's lock order (ApplyObjectChange, then the identity lock of ContactObjectType): the spaces, both rows in id order, the rows
        // merged into the source, the boards of the source's cards, then the directory; the fields are read under the row locks.
        var ids = new[] { command.SourceId, command.TargetId }.Order().ToArray();
        var spaces = await session.QueryOver<ContentResource>().WhereRestrictionOn(x => x.Id).IsIn(ids.Cast<object>().ToArray()).Select(x => x.SpaceId).ListAsync<Guid?>(ct);
        var actor = await access.LockActorInSpacesAsync(spaces.OfType<Guid>().ToArray(), ct);
        foreach (var id in ids)
        {
            await session.GetForUpdateAsync<ContentResource>(id, ct);
        }
        // The contacts merged into the source before, whose redirect moves below, locked before the identity lock: a restore of one holds
        // its row and then takes that lock (ObjectTrashHandler), so the other order would deadlock. A new one needs the source's row we hold.
        var mergedIn = await session.CreateSQLQuery("""
                SELECT "Id" FROM "ContentResource" WHERE "TypeId" = :type AND "Data" ->> 'mergedInto' = CAST(:source AS text) ORDER BY "Id"
                """)
            .SetParameter("source", command.SourceId).SetParameter("type", ContactObjectType.Id).ListAsync<Guid>(ct);
        foreach (var id in mergedIn)
        {
            await session.GetForUpdateAsync<ContentResource>(id, ct);
        }
        var boards = await session.CreateSQLQuery("""SELECT DISTINCT "BoardId" FROM "BoardItem" WHERE "TargetResourceId" = :source ORDER BY "BoardId" """)
            .SetParameter("source", command.SourceId).ListAsync<Guid>(ct);
        var locked = new List<Board>();
        foreach (var boardId in boards)
        {
            locked.Add((await session.GetAsync<Board>(boardId, LockMode.Upgrade, ct))!);
        }
        await directory.LockAsync(ct);
        // Live and readable as the actor reads it (on MCP not of a space switched out of AI, G5), and writable.
        var live = await directory.LiveAsync(actor, ids, ct);
        foreach (var id in new[] { command.SourceId, command.TargetId })
        {
            var allowed = live[id] is { State: "ok" } found && found.ContactId == id
                && await session.GetAsync<ContentResource>(id, ct) is { TypeId: ContactObjectType.Id, TrashedAt: null, Scope: not ObjectScopes.Private }
                ? await contacts.AuthorizeAsync(new ObjectActor(actor), id, ct)
                : ObjectAccess.None;
            if (allowed == ObjectAccess.None)
            {
                throw new ApiException(404, "Kontakt nebyl nalezen.", "contact_missing");
            }
            if (allowed != ObjectAccess.Write)
            {
                throw new ApiException(403, "Sloučit smí jen ten, kdo smí měnit oba kontakty.", "forbidden");
            }
        }
        var source = (await session.GetAsync<ContentResource>(command.SourceId, ct))!;
        var target = (await session.GetAsync<ContentResource>(command.TargetId, ct))!;
        var fields = Merged(JsonNode.Parse(target.Data!)!.AsObject(), JsonNode.Parse(source.Data!)!.AsObject());
        // An uploaded picture's address names the source: the target takes a copy under its own address.
        if (fields["photo"]?.GetValue<string>() is { } taken && ContactPhotos.IsUrl(taken))
        {
            fields.Remove("photo");
            if (await session.GetAsync<ContactPhoto>(source.Id, ct) is { } picture)
            {
                var copy = await session.GetAsync<ContactPhoto>(target.Id, ct) ?? new ContactPhoto { ContactId = target.Id };
                (copy.MimeType, copy.Sha256, copy.Content) = (picture.MimeType, picture.Sha256, picture.Content);
                await session.SaveOrUpdateAsync(copy, ct);
                fields["photo"] = ContactPhotos.Url(target.Id, picture.Sha256);
            }
        }
        // The cards of the source show the target, each board's as one batch its open canvases hear (BoardBatchWriter.RetargetAsync)
        // before the source's trash, so a canvas never shows the card as gone in between.
        var moved = 0;
        foreach (var board in locked)
        {
            moved += await boardWriter.RetargetAsync(board, actor.Id, source.Id, target.Id, ct);
        }
        await trash.Handle(new TrashObject(source.Id), ct);
        var data = JsonNode.Parse(source.Data!)!.AsObject();
        data[ContactDirectory.MergedInto] = target.Id.ToString();
        source.Data = data.ToJsonString();
        outbox.ObjectRevised(source.Id, source.TypeId);
        // The source's addresses are its no longer once it is in the trash: the target may take them.
        await session.FlushAsync(ct);
        if (fields.Count > 0)
        {
            await change.Handle(new ApplyObjectChange(target.Id, Guid.NewGuid(), null, new JsonObject { ["fields"] = fields }), ct);
        }
        // Every redirect is one hop: a contact merged into the source before follows the target now, as the relation sources read it.
        var redirected = await session.CreateSQLQuery("""
                UPDATE "ContentResource" SET "Data" = jsonb_set("Data", '{mergedInto}', to_jsonb(CAST(:target AS text)))
                WHERE "TypeId" = :type AND "Data" ->> 'mergedInto' = CAST(:source AS text)
                RETURNING "Id"
                """)
            .AddScalar("Id", NHibernateUtil.Guid)
            .SetParameter("target", target.Id).SetParameter("source", source.Id).SetParameter("type", ContactObjectType.Id).ListAsync<Guid>(ct);
        foreach (var id in redirected)
        {
            outbox.ObjectRevised(id, ContactObjectType.Id);
        }
        // Reference nodes of the tree name the target; the pages' revisions keep the source's id, which the mention relation follows, and
        // their mention rows are matched again (a snapshot matched by the source's address belongs to the target now).
        await session.CreateSQLQuery("""UPDATE "TreeReference" SET "TargetId" = :target WHERE "TargetId" = :source""")
            .SetParameter("target", target.Id).SetParameter("source", source.Id).ExecuteUpdateAsync(ct);
        await session.FlushAsync(ct);
        var mentioning = await session.CreateSQLQuery("""SELECT DISTINCT "SourcePageId" FROM "PageObjectMention" WHERE "ObjectId" = :source ORDER BY "SourcePageId" """)
            .AddScalar("SourcePageId", NHibernateUtil.Guid).SetParameter("source", source.Id).ListAsync<Guid>(ct);
        var mentions = new PageObjectMentions(session);
        foreach (var pageId in mentioning)
        {
            await mentions.RefreshAsync(pageId, ct);
        }
        return new MergedContacts(target.Id, ContactDirectory.Snapshot((await session.GetAsync<ContentResource>(target.Id, ct))!), moved);
    }

    /// <summary>The fields the target takes from the source: those it lacks, and the source's other addresses and phones; 409 when a
    /// list would exceed <see cref="ContactFields.MaxEntries"/> (the MCP preview reads the same refusal).</summary>
    public static JsonObject Merged(JsonObject target, JsonObject source)
    {
        var fields = new JsonObject();
        foreach (var field in ContactDirectory.Fields.Append("aliases").Where(field => source[field] is not null))
        {
            if (field is "emails" or "phones" or "aliases")
            {
                var have = (target[field] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToList();
                var extra = (source[field] as JsonArray ?? []).Select(x => x!.GetValue<string>())
                    .Where(value => field == "aliases" ? have.All(kept => ContactFields.Fold(kept) != ContactFields.Fold(value))
                        : !have.Contains(value, StringComparer.OrdinalIgnoreCase) && (field == "emails" || have.All(phone => EntityIdentity.Phone(phone) is not { } e164 || e164 != EntityIdentity.Phone(value))))
                    .ToArray();
                // Refused before any change: cutting the union would drop the source's values with the source in the trash.
                if (have.Count + extra.Length > ContactFields.MaxEntries)
                {
                    throw new ApiException(409, $"Kontakty nejde sloučit: pole „{ContactFields.Labels[field]}“ by mělo {have.Count + extra.Length} hodnot " +
                        $"(cílový kontakt {have.Count}, ze zdrojového {extra.Length} dalších), kontakt jich unese nejvýše {ContactFields.MaxEntries}. " +
                        "Nejdřív u jednoho z nich některé odeberte.", "contact_merge_over_capacity");
                }
                if (extra.Length > 0)
                {
                    fields[field] = new JsonArray(have.Concat(extra).Select(value => (JsonNode)JsonValue.Create(value)).ToArray());
                }
            }
            else if (target[field] is null && field != "name")
            {
                fields[field] = source[field]!.DeepClone();
            }
        }
        return fields;
    }
}

internal static class ContactRequests
{
    public static ApiException Missing() => new(400, "Chybí kontakt (contact s polem name).", "invalid_argument");
}
