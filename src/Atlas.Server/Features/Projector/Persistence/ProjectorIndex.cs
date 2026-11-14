using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Platform.Events;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector.Persistence;

/// <summary>
/// The requirement index (<c>ProjectorRequirementIndex</c>: one row per requirement of a page's working and published revision, as
/// <see cref="RequirementTree.Read"/> reads it, both shapes) kept in C# since migration 2026110870 dropped its triggers (owner: the database
/// holds no logic). <c>ProjectorIndexedRevision</c> marks the revisions a page's rows were read from.
/// Consistency: the document is never stale; the index follows it eventually (milliseconds) and is never read stale. Every revision write
/// announces itself (ContentRevised, RevisionPublicationCoverageTests), and <see cref="ProjectorIndexRevision"/> refreshes the page from the
/// outbox after the commit; every read first refreshes, in the read's transaction, the pages whose marks do not match their current working
/// and published revisions (<see cref="EnsureFreshAsync"/>), so a read sees its own writes even before the outbox runs, and a host without
/// the outbox (tests) needs nothing more. A rewrite in place keeps the revision's id: it announces itself (the conversion also drops its
/// pages' marks) and is seen after the outbox's delivery. The full rebuild is the job <see cref="ProjectorIndexJob"/>.
/// </summary>
public static class ProjectorIndex
{
    /// <summary>Refreshes the pages among <paramref name="pages"/> (null: those of <paramref name="space"/>, or every page) whose index does not match their current revisions:
    /// a current revision without a mark, a mark of a revision no longer current, or a row whose working or published flag changed.</summary>
    // shortcut: a read across all pages (a contact's uses, the dependents of a requirement) checks every page's marks, a few ms per
    // thousand pages; keep a pending flag per page if that ever shows.
    public static async Task EnsureFreshAsync(ISession session, IReadOnlyCollection<Guid>? pages, CancellationToken ct, Guid? space = null)
    {
        var scope = pages is not null ? $"p.\"Id\" = {GuidList.Any(":pages")}" : space is not null ? "p.\"SpaceId\" = :space" : "TRUE";
        var query = session.CreateSQLQuery($"""
            WITH current AS MATERIALIZED (
              SELECT p."Id" AS "PageId", r."Id" AS "RevisionId", r."ActiveTo" IS NULL AS "IsWorking", r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId" AS "IsPublished"
              FROM "Page" p JOIN "PageRevision" r ON r."PageId" = p."Id" AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId")
              WHERE p."PurgedAt" IS NULL AND {scope})
            SELECT c."PageId" FROM current c
            WHERE NOT EXISTS (SELECT 1 FROM "ProjectorIndexedRevision" x WHERE x."PageId" = c."PageId" AND x."RevisionId" = c."RevisionId")
              OR EXISTS (SELECT 1 FROM "ProjectorRequirementIndex" i WHERE i."PageId" = c."PageId" AND i."RevisionId" = c."RevisionId"
                AND (i."IsWorking", i."IsPublished") IS DISTINCT FROM (c."IsWorking", c."IsPublished"))
            UNION
            SELECT x."PageId" FROM "ProjectorIndexedRevision" x JOIN "Page" p ON p."Id" = x."PageId"
            WHERE {scope} AND NOT EXISTS (SELECT 1 FROM current c WHERE c."PageId" = x."PageId" AND c."RevisionId" = x."RevisionId")
            """).AddScalar("PageId", NHibernateUtil.Guid);
        if (space is { } spaceId && pages is null)
        {
            query.SetParameter("space", spaceId);
        }
        if (pages is not null)
        {
            if (pages.Count == 0)
            {
                return;
            }
            query.SetParameter("pages", GuidList.Param(pages));
        }
        var stale = (await query.ListAsync<Guid>(ct)).Distinct().ToList();
        if (stale.Count > 0)
        {
            await RefreshAsync(session, stale, ct, wait: true);
        }
    }

    /// <summary>Replaces the rows and marks of <paramref name="pages"/> by their working and published revisions as they are now, one page
    /// at a time under the page's index lock, so two refreshes never interleave. The outbox and the job only try the lock: a page another
    /// transaction is refreshing right now is left to it (its rows land with its commit). A read (<paramref name="wait"/>) waits for that
    /// transaction and refreshes the page itself, as it must not read rows it cannot see yet (review P2-4); only the outbox and the job
    /// never wait, so a read waits at most for one refresh or one batch. Two reads that each refreshed a page the other then needs
    /// (pages taken over separate calls of one transaction) are a deadlock PostgreSQL ends for one of them (40P01).
    /// Lock order: the page's row first, as every writer of a page takes it (FOR UPDATE, then the page's marks: the requirement
    /// conversion), then the index lock and rows. Taken FOR KEY SHARE, the lock the rows' foreign key takes anyway, but before any row of
    /// the index: a refresh that held the page's marks while waiting for the row deadlocked with the conversion deleting them (4.0 first
    /// start). The outbox and the job skip a page a writer holds: its new revision announces itself, a rewrite in place drops the page's marks, and
    /// a read refreshes a page whose marks do not match first.</summary>
    public static async Task RefreshAsync(ISession session, IReadOnlyCollection<Guid> pages, CancellationToken ct, bool wait = false)
    {
        foreach (var page in pages.Distinct().Order())
        {
            if (await session.CreateSQLQuery($"""SELECT "Id" FROM "Page" WHERE "Id" = :page FOR KEY SHARE{(wait ? "" : " SKIP LOCKED")}""")
                    .AddScalar("Id", NHibernateUtil.Guid).SetParameter("page", page).UniqueResultAsync<Guid?>(ct) is null)
            {
                continue;
            }
            if (!await session.CreateSQLQuery(wait
                        ? """SELECT true AS "Held" FROM pg_advisory_xact_lock(hashtextextended(:key, 812))"""
                        : """SELECT pg_try_advisory_xact_lock(hashtextextended(:key, 812)) AS "Held" """)
                    .AddScalar("Held", NHibernateUtil.Boolean).SetParameter("key", "atlas.projector-index:" + page.ToString("D")).UniqueResultAsync<bool>(ct))
            {
                continue;
            }
            var revisions = await session.CreateSQLQuery("""
                    SELECT r."Id", r."ActiveTo" IS NULL AS "IsWorking", r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId" AS "IsPublished", r."ContentJson"
                    FROM "Page" p JOIN "PageRevision" r ON r."PageId" = p."Id" AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId")
                    WHERE p."Id" = :page AND p."PurgedAt" IS NULL
                    """).AddScalar("Id", NHibernateUtil.Guid).AddScalar("IsWorking", NHibernateUtil.Boolean).AddScalar("IsPublished", NHibernateUtil.Boolean)
                .AddScalar("ContentJson", NHibernateUtil.String).SetParameter("page", page).ListAsync<object[]>(ct);
            var rows = new JsonArray();
            foreach (var row in revisions)
            {
                var (revision, working, published, json) = ((Guid)row[0], (bool)row[1], (bool)row[2], (string)row[3]);
                foreach (var requirement in Read(json, page))
                {
                    rows.Add(new JsonObject
                    {
                        ["revision"] = revision, ["position"] = requirement.Position, ["working"] = working, ["published"] = published,
                        ["blockId"] = requirement.BlockId, ["key"] = requirement.Key, ["title"] = requirement.Title, ["direction"] = requirement.Direction,
                        ["state"] = requirement.State, ["date"] = requirement.Date, ["party"] = requirement.Party, ["where"] = requirement.Where,
                        ["requires"] = string.Join(",", requirement.Requires), ["parent"] = requirement.ParentBlockId, ["contact"] = requirement.PartyContactId,
                    });
                }
            }
            await session.CreateSQLQuery("""
                    DELETE FROM "ProjectorRequirementIndex" WHERE "PageId" = :page;
                    DELETE FROM "ProjectorIndexedRevision" WHERE "PageId" = :page;
                    INSERT INTO "ProjectorIndexedRevision"("PageId","RevisionId") SELECT :page, CAST(id AS uuid) FROM unnest(string_to_array(:revisions, ',')) id WHERE id <> '';
                    INSERT INTO "ProjectorRequirementIndex"("PageId","RevisionId","Position","IsWorking","IsPublished","BlockId","Key","Title","Direction",
                      "State","Date","Party","Where","Requires","ParentBlockId","PartyContactId")
                    SELECT :page, x."revision", x."position", x."working", x."published", x."blockId", x."key", x."title", x."direction", x."state",
                      x."date", x."party", x."where", x."requires", x."parent", x."contact"
                    FROM jsonb_to_recordset(CAST(:rows AS jsonb)) AS x("revision" uuid, "position" integer, "working" boolean, "published" boolean,
                      "blockId" text, "key" text, "title" text, "direction" text, "state" text, "date" text, "party" text, "where" text, "requires" text,
                      "parent" text, "contact" text)
                    """).SetParameter("page", page).SetParameter("revisions", string.Join(",", revisions.Select(x => ((Guid)x[0]).ToString("D"))))
                .SetParameter("rows", rows.ToJsonString()).ExecuteUpdateAsync(ct);
        }
    }

    /// <summary>The rows of one stored revision: none without a requirement block or a requirement heading (the old trigger's test), none
    /// for JSON that does not parse.</summary>
    private static IReadOnlyList<Contracts.Requirement> Read(string json, Guid page)
    {
        if (!json.Contains("\"requirement\"", StringComparison.Ordinal) && !json.Contains("\"state\"", StringComparison.Ordinal))
        {
            return [];
        }
        try
        {
            return JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions) is JsonObject rich ? RequirementTree.Read(rich, page) : [];
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }
}

/// <summary>The index's own delivery of <see cref="ContentRevised"/> of pages: it reads the page's current state, so an older delivery after a
/// newer one changes nothing, and repeated ones for one page cost one refresh each.</summary>
public sealed record ProjectorIndexRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message.ContentType == Pages.PagesAtlasModule.ContentType;
}

public static class ProjectorIndexRevisionHandler
{
    public static Task Handle(ProjectorIndexRevision delivery, ISession session, CancellationToken ct)
        => ProjectorIndexRevision.Takes(delivery.Event) ? ProjectorIndex.RefreshAsync(session, [delivery.Event.ResourceId], ct) : Task.CompletedTask;
}
