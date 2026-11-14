using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.Entities;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts;

/// <summary>One person of the migration: the snapshots of one identity, where they are and what becomes of them.</summary>
/// <param name="SpaceId">A created contact's space when it is a contact of that space rather than of the wiki.</param>
public sealed record ContactLinkItem(string Name, string IdentityKey, int Occurrences, IReadOnlyList<Guid> PageIds, Guid? ContactId = null, string? Reason = null,
    Guid? SpaceId = null);

/// <param name="Pages">The live pages holding a contact.</param>
/// <param name="Revisions">Their revisions holding a contact still to link.</param>
/// <param name="Restricted">People without a match written only where not every reader of a space reads them (a page with its own
/// access, a working revision, a template): no contact is created from them and they stay unlinked (null in runs recorded before).</param>
public sealed record ContactLinkReport(string Mode, IReadOnlyList<ContactLinkItem> Linked, IReadOnlyList<ContactLinkItem> Created,
    IReadOnlyList<ContactLinkItem> Unclear, int Pages, int Revisions, IReadOnlyList<ContactLinkItem>? Restricted = null);

/// <summary>The snapshots of one identity collected so far, in page order: the first, its pages, and the first copy of each readership.</summary>
public sealed class ContactLinkGroup
{
    public required JsonObject First { get; init; }
    public int Occurrences { get; set; }
    public List<Guid> PageIds { get; init; } = [];
    /// <summary>A copy differs from the first (ContactDirectory.Conflicts), across batches too.</summary>
    public bool Inconsistent { get; set; }
    /// <summary>The first copy every wiki reader reads.</summary>
    public JsonObject? Public { get; set; }
    /// <summary>The copies every reader of a space reads, by space: their number and the first.</summary>
    public Dictionary<Guid, ContactLinkCopies> Spaces { get; init; } = [];
}

public sealed class ContactLinkCopies
{
    public required JsonObject First { get; init; }
    public int Count { get; set; }
}

/// <summary>The plan of a run as its checkpoint between bounded steps (<see cref="ContactLinkMigration.PrepareAsync"/>): the identities of the
/// pages up to <see cref="AfterPageId"/>, and once every page is read (<see cref="Collected"/>), the decisions of the identities up to
/// <see cref="AfterKey"/> (ordinal), <see cref="Ids"/> the contact of every one to link; <see cref="Done"/> when all are decided.</summary>
public sealed record ContactLinkPreparation(Guid AfterPageId, bool Collected, int Pages, int Revisions, Dictionary<string, ContactLinkGroup> Groups, string? AfterKey,
    bool Done, List<ContactLinkItem> Linked, List<ContactLinkItem> Created, List<ContactLinkItem> Unclear, List<ContactLinkItem> Restricted, Dictionary<string, Guid> Ids)
{
    public static ContactLinkPreparation Start() => new(Guid.Empty, false, 0, 0, [], null, false, [], [], [], [], []);

    public ContactLinkReport ToReport(string mode) => new(mode, Linked, Created, Unclear, Pages, Revisions, Restricted);
}

/// <summary>
/// The migration of the contacts written into pages (contacts spec §6, K4): every contact without <c>objectId</c> in the working and
/// the published revision of a live page, paired by identity. <c>linked</c>: a strong match in the directory; <c>created</c>: no match,
/// one new contact per identity, made of a snapshot every wiki reader reads (a wiki contact), else of one every reader of its space reads
/// (a contact of that space: the owner's rule 2026-10-30, a reader sees what is in or linked into a space they read; a linked one names
/// the fields its pages wrote otherwise; <c>restricted</c>: no match and no such snapshot); <c>unclear</c>: only a weak match, snapshots
/// of one identity that differ among themselves, or fields the directory refuses, left as
/// they are. <c>report</c> (<see cref="ReportAsync"/>) writes nothing; <c>apply</c> plans and creates the contacts at its start, a bounded
/// step at a time (<see cref="PrepareAsync"/>), and then writes the <c>objectId</c> into the revisions in place (the text stays), deleting nothing, a batch of pages at a time
/// (<see cref="LinkBatchAsync"/>, the background run of ContactLinkRuns), and rebuilds the entity catalog at its end.
/// </summary>
public sealed class ContactLinkMigration(ISession session, IAtlasLocks locks, ContactDirectory directory, CreateObjectHandler create)
{
    /// <summary>The pages one transaction of the run rewrites, and the pages or identities one step of its plan reads or decides.</summary>
    public const int BatchPages = 50;

    /// <summary>The report (<c>report</c>): the whole plan in one step, without the directory's lock and without creating.</summary>
    public async Task<ContactLinkReport> ReportAsync(CancellationToken ct)
        => (await PrepareAsync(ContactLinkPreparation.Start(), false, int.MaxValue, ct)).ToReport("report");

    /// <summary>One bounded step of the plan from its checkpoint: the identities of the next <paramref name="size"/> pages, and once every
    /// page is read, what becomes of the next <paramref name="size"/> identities; with <paramref name="createContacts"/> their new contacts
    /// are created under the directory's lock, which only this step's transaction holds.</summary>
    public async Task<ContactLinkPreparation> PrepareAsync(ContactLinkPreparation plan, bool createContacts, int size, CancellationToken ct)
    {
        if (!plan.Collected)
        {
            var revisions = await RevisionsAsync(plan.AfterPageId, size, null, ct);
            var holding = 0;
            foreach (var revision in revisions)
            {
                var unlinked = revision.Lists.SelectMany(list => list).OfType<JsonObject>().Where(Unlinked).ToList();
                holding += unlinked.Count > 0 ? 1 : 0;
                foreach (var snapshot in unlinked)
                {
                    Collect(plan.Groups, snapshot, revision.PageId, revision.Public, revision.Space);
                }
            }
            var pages = revisions.Select(revision => revision.PageId).Distinct().Count();
            plan = plan with
            {
                AfterPageId = revisions.Count > 0 ? revisions[^1].PageId : plan.AfterPageId, Collected = pages < size, Pages = plan.Pages + pages,
                Revisions = plan.Revisions + holding
            };
            if (!plan.Collected)
            {
                return plan;
            }
        }
        var after = plan.AfterKey;
        var next = plan.Groups.Keys.Where(key => after is null || string.CompareOrdinal(key, after) > 0).Order(StringComparer.Ordinal).ToList();
        var batch = next.Take(size).ToList();
        if (createContacts && batch.Count > 0)
        {
            await directory.LockAsync(ct);
        }
        foreach (var key in batch)
        {
            await DecideAsync(plan, key, plan.Groups[key], createContacts, ct);
        }
        return plan with { AfterKey = batch.Count > 0 ? batch[^1] : after, Done = next.Count <= size };
    }

    private static void Collect(Dictionary<string, ContactLinkGroup> groups, JsonObject snapshot, Guid page, bool open, Guid? space)
    {
        // A copy: the checkpoint holds the snapshot, not the document it is in.
        var copy = snapshot.DeepClone().AsObject();
        var key = EntityIdentity.Of(snapshot);
        if (!groups.TryGetValue(key, out var group))
        {
            groups[key] = group = new ContactLinkGroup { First = copy };
        }
        else if (ContactDirectory.Conflicts(group.First, snapshot).Count > 0)
        {
            group.Inconsistent = true;
        }
        group.Occurrences++;
        if (group.PageIds.Count == 0 || group.PageIds[^1] != page)
        {
            group.PageIds.Add(page);
        }
        if (open)
        {
            group.Public ??= copy;
        }
        if (space is { } id)
        {
            (group.Spaces.TryGetValue(id, out var copies) ? copies : group.Spaces[id] = new ContactLinkCopies { First = copy }).Count++;
        }
    }

    /// <summary>What becomes of one identity: linked, created (with <paramref name="createContacts"/>), unclear or restricted.</summary>
    private async Task DecideAsync(ContactLinkPreparation plan, string key, ContactLinkGroup group, bool createContacts, CancellationToken ct)
    {
        var first = group.First;
        var item = new ContactLinkItem(Text(first["name"]), key, group.Occurrences, group.PageIds.ToArray());
        if (group.Inconsistent)
        {
            plan.Unclear.Add(item with { Reason = "inconsistent" });
            return;
        }
        var match = await directory.MatchUnfilteredAsync(first, null, ct);
        if (match.Strong is { } strong)
        {
            // The same address or account is the same person: linked, the directory holds, and what the page wrote otherwise is reported.
            plan.Linked.Add(item with
            {
                ContactId = plan.Ids[key] = strong.Id,
                Reason = match.Conflicts.Count > 0 ? "conflict:" + string.Join(",", match.Conflicts.Select(conflict => conflict.Field)) : null
            });
            return;
        }
        if (match.Weak.Count > 0)
        {
            plan.Unclear.Add(item with { ContactId = match.Weak[0].Id, Reason = "possible_duplicate" });
            return;
        }
        // A wiki contact is read by every account: it is made only of what every account reads already. Else a contact of the space
        // where most of its readable copies are, read by that space's readers only, as the page it comes from.
        var space = group.Public is not null ? null
            : group.Spaces.OrderByDescending(copies => copies.Value.Count).ThenBy(copies => copies.Key).Select(copies => (Guid?)copies.Key).FirstOrDefault();
        if ((group.Public ?? (space is { } id ? group.Spaces[id].First : null)) is not { } open)
        {
            plan.Restricted.Add(item with { Reason = "restricted" });
            return;
        }
        JsonObject fields;
        try
        {
            fields = ContactLinker.DirectoryFields(open, null);
        }
        catch (ApiException)
        {
            plan.Unclear.Add(item with { Reason = "invalid" });
            return;
        }
        if (createContacts)
        {
            plan.Ids[key] = (await create.Handle(new CreateObject(ContactObjectType.Id, space is null ? ObjectScopes.Wiki : ObjectScopes.Space, space, fields), ct)).Id;
            // The next identity's match reads the directory by SQL.
            await session.FlushAsync(ct);
        }
        plan.Created.Add(item with { ContactId = plan.Ids.GetValueOrDefault(key) is var made && made != Guid.Empty ? made : null, SpaceId = space });
    }

    /// <summary>Writes the ids of <paramref name="ids"/> into the revisions of the next <paramref name="pages"/> pages after
    /// <paramref name="after"/> (by id). The mention projection of each changed page is refreshed here, in the batch's transaction (no trigger does).</summary>
    /// <returns>The pages read, the last of them (where the next batch starts) and the revisions written.</returns>
    public async Task<(int Pages, Guid Last, int Written)> LinkBatchAsync(IReadOnlyDictionary<string, Guid> ids, Guid after, int pages, CancellationToken ct)
    {
        // The pages FOR UPDATE in id order before their revisions are read, as a save takes its page (PageGate.EditAsync): a save waits
        // for the batch and then reads the linked revision, instead of writing back the one it read before.
        await locks.AcquireContentSharedAsync(ct);
        var batch = (await session.CreateSQLQuery(NextPages).AddScalar("PageId", NHibernateUtil.Guid)
            .SetParameter("after", after).SetParameter("pages", pages).ListAsync<Guid>(ct)).ToList();
        var revisions = await RevisionsAsync(after, pages, await locks.LockPagesForUpdateAsync(batch, ct), ct);
        // 3.6-H: a working revision whose page has a live document is that document's checkpoint, and the next checkpoint would write the
        // live content back over a link made here. Such a page keeps it; its editor links contacts itself (the plugin's link option).
        var live = batch.Count == 0 ? [] : (await session.CreateSQLQuery($"""
                SELECT r."Id" FROM "PageRevision" r JOIN "PageCollaboration" c ON c."PageId" = r."PageId"
                WHERE r."ActiveTo" IS NULL AND c."State" IS NOT NULL AND r."PageId" = {GuidList.Any(":pages")}
                """).AddScalar("Id", NHibernateUtil.Guid).SetParameter("pages", GuidList.Param(batch)).ListAsync<Guid>(ct)).ToHashSet();
        var written = 0;
        var changedPages = new SortedSet<Guid>();
        foreach (var (id, page, root, lists, _, _) in revisions.Where(revision => !live.Contains(revision.Id)))
        {
            var changed = false;
            foreach (var list in lists)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is JsonObject snapshot && Unlinked(snapshot) && ids.TryGetValue(EntityIdentity.Of(snapshot), out var contactId))
                    {
                        list[i] = ContactLinker.WithId(snapshot, contactId);
                        changed = true;
                    }
                }
            }
            if (!changed)
            {
                continue;
            }
            var json = root.ToJsonString(RichDocument.JsonWrite);
            string markdown;
            try
            {
                markdown = Rules.ValidateDocument(json).Markdown;
            }
            catch (ApiException)
            {
                // A revision stored under older rules stays as it is rather than failing every other page's link.
                continue;
            }
            await session.CreateSQLQuery("""UPDATE "PageRevision" SET "ContentJson" = :json, "Markdown" = :markdown WHERE "Id" = :id""")
                .SetParameter("json", json).SetParameter("markdown", markdown).SetParameter("id", id)
                .ExecuteUpdateAsync(ct);
            written++;
            changedPages.Add(page);
        }
        var mentions = new PageObjectMentions(session);
        foreach (var page in changedPages)
        {
            await mentions.RefreshAsync(page, ct);
        }
        if (written > 0)
        {
            await Search.SearchDocuments.RefreshAsync(session, batch, ct);
        }
        return (batch.Count, batch.Count > 0 ? batch[^1] : after, written);
    }

    /// <summary>The working and published revisions holding a contact of the first <paramref name="pages"/> live pages after
    /// <paramref name="after"/> by page id (or of the pages <paramref name="only"/>), each with the contact lists of its document and
    /// whether every wiki reader reads it: the published revision of a page that is no template, in a space outside the trash, that every
    /// active account reads (EffectivePageAccess); else, for such a published revision of a page without its own access, its space, whose
    /// every reader reads it.</summary>
    private async Task<List<(Guid Id, Guid PageId, JsonNode Root, List<JsonArray> Lists, bool Public, Guid? Space)>> RevisionsAsync(Guid after, int pages,
        IReadOnlyList<Guid>? only, CancellationToken ct)
    {
        var next = only is null
            ? NextPages
            : $"""SELECT unnest(CAST(string_to_array(:only, ',') AS uuid[])) AS "PageId" """;
        var query = session.CreateSQLQuery($"""
                WITH next AS ({next}),
                everyone AS (
                  SELECT e."PageId" FROM "EffectivePageAccess" e WHERE e."PageId" IN (SELECT "PageId" FROM next) GROUP BY e."PageId"
                  HAVING count(*) = (SELECT count(*) FROM "UserAccount" WHERE "IsActive")
                )
                SELECT r."Id", r."PageId", r."ContentJson", shared AND p."Id" IN (SELECT "PageId" FROM everyone) AS "Public",
                  CASE WHEN shared AND NOT p."OwnAccess" THEN p."SpaceId" END AS "Space"
                FROM next n JOIN "PageRevision" r ON r."PageId" = n."PageId" JOIN "Page" p ON p."Id" = r."PageId" JOIN "Space" s ON s."Id" = p."SpaceId",
                  LATERAL (SELECT r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId" AND NOT p."IsTemplate" AND s."TrashedAt" IS NULL AS shared) published
                WHERE {Holds}
                ORDER BY r."PageId", r."Id"
                """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("ContentJson", NHibernateUtil.String)
            .AddScalar("Public", NHibernateUtil.Boolean).AddScalar("Space", NHibernateUtil.Guid);
        var rows = await (only is null ? query.SetParameter("after", after).SetParameter("pages", pages) : query.SetParameter("only", GuidList.Param(only)))
            .ListAsync<object[]>(ct);
        return rows.Select(row =>
        {
            var root = JsonNode.Parse((string)row[2], documentOptions: new JsonDocumentOptions { MaxDepth = MarkdownDocumentValidator.JsonMaxDepth })!;
            var lists = new List<JsonArray>();
            ContactLinker.Collect(root["document"], lists);
            return ((Guid)row[0], (Guid)row[1], root, lists, (bool)row[3], (Guid?)row[4]);
        }).ToList();
    }

    private const string Holds = """NOT p."IsTrashed" AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId") AND r."ContentJson" LIKE '%"contact%'""";

    /// <summary>The first <c>:pages</c> live pages after <c>:after</c> by id with a revision holding a contact.</summary>
    private const string NextPages = $"""SELECT DISTINCT r."PageId" FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId" WHERE {Holds} AND r."PageId" > :after ORDER BY r."PageId" LIMIT :pages""";

    private static bool Unlinked(JsonObject snapshot) => Text(snapshot["name"]).Length > 0 && Text(snapshot["objectId"]).Length == 0 && !ContactSnapshots.IsUnresolved(snapshot);

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
}
