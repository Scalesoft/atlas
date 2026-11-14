using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Content;
using NHibernate;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector.Persistence;

/// <summary>
/// Reads of the requirement index (migration 2026102800). A page shows its working rows to whoever may edit it and its published
/// rows to anyone else who may read it, as PageReader does. The space reads decide that in SQL for every page at once, with the
/// rule PageLinkStore uses for listing a page: an untrashed, non-template page with a page role, and an unpublished one only for
/// its editing owner or a WikiAdmin. One statement whatever the number of pages, and only the rows the caller keeps.
/// </summary>
public sealed class RequirementIndex(ISession session)
{
    /// <summary>Which rows a space read keeps (spec §4), decided in SQL; fixed fragments, never input.</summary>
    public static string Filter(string filter) => filter switch
    {
        "all" => """i."Key"<>''""",
        // Every block, keyless ones too (QueryRequirements filters in memory).
        "every" => "TRUE",
        "missing" => """i."Key"<>'' AND i."State" NOT IN ('done','not_applicable')""",
        "inbound-missing" => """i."Key"<>'' AND i."Direction"='inbound' AND i."State" NOT IN ('done','not_applicable')""",
        "waiting" => """i."State"='waiting_for_input'""",
        _ => throw new ApiException(400, "Filtr přehledu je all, missing nebo inbound-missing.", "invalid_filter")
    };

    private const string Columns = """
        i."BlockId",i."Key",i."Title",i."Direction",i."State",i."Date",i."Party",i."Where",i."Requires",i."ParentBlockId",i."Position",i."PartyContactId"
        """;

    public async Task<IReadOnlyList<Requirement>> PageAsync(Guid pageId, bool working, CancellationToken ct)
    {
        await ProjectorIndex.EnsureFreshAsync(session, [pageId], ct);
        var rows = await Scalars(session.CreateSQLQuery($"""
                SELECT {Columns} FROM "ProjectorRequirementIndex" i
                WHERE i."PageId"=:page AND CASE WHEN :working THEN i."IsWorking" ELSE i."IsPublished" END
                ORDER BY i."Position"
                """))
            .SetParameter("page", pageId).SetParameter("working", working)
            .ListAsync<object[]>(ct);
        return rows.Select(row => Read(row, 0)).ToArray();
    }

    /// <summary>The blocks that pass <paramref name="filter"/> (<see cref="Filter"/>, the only place a filter is decided) of every
    /// deployment page of the space the actor may read (a catalog page is none, review U2-5), pages in the order of the titles they read,
    /// each row with its page's done and applicable blocks (C2-13), counted before the filter. With <paramref name="words"/> (SearchText.LookupWords)
    /// only the blocks whose title, key or page title holds each of them, and at most <paramref name="limit"/> rows; the page
    /// <paramref name="excludePageId"/> is left out before the limit (the picker's own page, whose blocks it has already).</summary>
    public async Task<IReadOnlyList<RequirementRow>> SpaceAsync(UserAccount actor, Guid spaceId, string filter, CancellationToken ct,
        IReadOnlyList<string>? words = null, int limit = int.MaxValue, Guid? excludePageId = null)
    {
        var kept = Filter(filter);
        await ProjectorIndex.EnsureFreshAsync(session, null, ct, spaceId);
        var rows = await Scalars(session.CreateSQLQuery($"""
                WITH {AccessSql.ActorCte}, access AS MATERIALIZED (
                  SELECT e."PageId",e."Role" FROM "EffectivePageAccess" e WHERE e."UserId"=:actor AND e."SpaceId"=:space
                ), visible AS MATERIALIZED (
                  SELECT p."Id",(NOT :published AND (a."IsWikiAdmin" OR coalesce(e."Role",0)>=2
                    OR p."PublishedRevisionId" IS NOT NULL AND coalesce(p."PublicationMode",s."PublicationMode")='Live')) AS "Working"
                  FROM "Page" p JOIN "Space" s ON s."Id"=p."SpaceId" CROSS JOIN actor a LEFT JOIN access e ON e."PageId"=p."Id"
                  WHERE p."SpaceId"=:space AND NOT p."IsTrashed" AND NOT p."IsTemplate" AND (a."IsWikiAdmin" OR e."Role">=1) AND p."Id"<>:exclude
                    AND (p."PublishedRevisionId" IS NOT NULL OR NOT :published AND (a."IsWikiAdmin" OR e."Role">=2) AND (p."OwnerId"=a."Id" OR a."IsWikiAdmin"))
                    AND NOT EXISTS (SELECT 1 FROM "ProjectorCatalogPage" c WHERE c."PageId"=p."Id")
                ), shown AS (
                  SELECT i.*,count(*) FILTER (WHERE i."State"='done') OVER page AS "PageDone",
                    count(*) FILTER (WHERE i."State"<>'not_applicable') OVER page AS "PageApplicable"
                  FROM visible v
                  JOIN "ProjectorRequirementIndex" i ON i."PageId"=v."Id" AND CASE WHEN v."Working" THEN i."IsWorking" ELSE i."IsPublished" END
                  WINDOW page AS (PARTITION BY i."PageId")
                )
                SELECT i."PageId",i."RevisionId",r."Title" AS "PageTitle",{Columns},i."PageDone",i."PageApplicable"
                FROM shown i JOIN "PageRevision" r ON r."Id"=i."RevisionId"
                WHERE {kept} AND NOT EXISTS (SELECT 1 FROM unnest(string_to_array(:words, E'\n')) w
                  WHERE strpos(lower(public.unaccent(i."Title"||' '||i."Key"||' '||r."Title")),w)=0)
                ORDER BY r."Title",i."PageId",i."Position"
                LIMIT :limit
                """).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("RevisionId", NHibernateUtil.Guid).AddScalar("PageTitle", NHibernateUtil.String))
            .AddScalar("PageDone", NHibernateUtil.Int64).AddScalar("PageApplicable", NHibernateUtil.Int64)
            .SetParameter("actor", actor.Id).SetParameter("stamp", actor.SecurityStamp).SetParameter("space", spaceId)
            .SetParameter("words", string.Join('\n', words ?? [])).SetParameter("limit", limit)
            .SetParameter("exclude", excludePageId ?? Guid.Empty).SetParameter("published", ProjectorPublishedReads.Active)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new RequirementRow((Guid)row[0], (string)row[2], Read(row, 3))
        {
            RevisionId = (Guid)row[1], PageDone = (int)(long)row[15], PageApplicable = (int)(long)row[16]
        }).ToArray();
    }

    /// <summary>Who changed each of <paramref name="blocks"/> last (plan 3.6): of the page's revisions up to <paramref name="version"/>, the
    /// newest whose change summary touched the block (RevisionChanges.Touched), or the newest saved without a summary (before 3.6, an
    /// import) when that is newer, as nothing tells what it changed. One statement for the page; by block id.</summary>
    public async Task<IReadOnlyDictionary<string, RequirementChangeInfo>> LastChangesAsync(Guid pageId, int version, bool working,
        IReadOnlyList<Requirement> blocks, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
                WITH touched AS (
                  SELECT DISTINCT ON (t."BlockId") t."BlockId",r."Version",r."AuthorId",r."ActiveFrom"
                  FROM "PageRevision" r CROSS JOIN LATERAL jsonb_array_elements_text(r."ChangeSummary"->'touched') t("BlockId")
                  WHERE r."PageId"=:page AND r."Version"<=:version
                  ORDER BY t."BlockId",r."Version" DESC
                ), untracked AS (
                  SELECT CAST(NULL AS text) AS "BlockId",r."Version",r."AuthorId",r."ActiveFrom" FROM "PageRevision" r
                  WHERE r."PageId"=:page AND r."Version"<=:version AND r."ChangeSummary" IS NULL
                  ORDER BY r."Version" DESC LIMIT 1
                )
                SELECT x."BlockId",x."Version",x."AuthorId",coalesce(a."DisplayName",'') AS "ChangedBy",x."ActiveFrom"
                FROM (SELECT * FROM touched UNION ALL SELECT * FROM untracked) x LEFT JOIN "UserAccount" a ON a."Id"=x."AuthorId"
                """)
            .AddScalar("BlockId", NHibernateUtil.String).AddScalar("Version", NHibernateUtil.Int32).AddScalar("AuthorId", NHibernateUtil.Guid)
            .AddScalar("ChangedBy", NHibernateUtil.String).AddScalar("ActiveFrom", NHibernateUtil.UtcDateTime)
            .SetParameter("page", pageId).SetParameter("version", version).ListAsync<object[]>(ct);
        var untracked = rows.FirstOrDefault(row => row[0] is null);
        var touched = rows.Where(row => row[0] is not null).ToDictionary(row => (string)row[0], StringComparer.Ordinal);
        var kind = working ? "save" : "publish";
        var changes = new Dictionary<string, RequirementChangeInfo>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            var row = touched.GetValueOrDefault(block.BlockId) is { } last && (untracked is null || (int)last[1] > (int)untracked[1]) ? last : untracked;
            if (row is not null)
            {
                changes.TryAdd(block.BlockId, new RequirementChangeInfo(kind, (Guid)row[2], (string)row[3], (DateTime)row[4], block.Title, block.State));
            }
        }
        return changes;
    }

    /// <summary>The state changes of block <paramref name="blockId"/> in the page's versions up to <paramref name="version"/>, newest first:
    /// each version's change as its newest revision there sums it (RevisionChanges.States, against the version before), by the author of
    /// the version's last checkpoint that touched the block (the version's newest otherwise), and when; at most <paramref name="limit"/>.
    /// One pass over the page's revisions finds each version's newest and newest touching one (review P2-6: a lookup per version was quadratic).</summary>
    public async Task<IReadOnlyList<(string From, string To, Guid AuthorId, string ChangedBy, DateTime ChangedAt)>> StateChangesAsync(Guid pageId,
        string blockId, int version, CancellationToken ct, int? limit = null)
    {
        var rows = await session.CreateSQLQuery("""
                WITH revisions AS MATERIALIZED (
                  SELECT r."Version",r."GroupId",r."AuthorId",r."ActiveFrom",r."ChangeSummary" FROM "PageRevision" r
                  WHERE r."PageId"=:page AND r."Version"<=:version AND r."ChangeSummary" IS NOT NULL
                ), groups AS (
                  SELECT max("Version") AS "Head",max("Version") FILTER (WHERE jsonb_exists("ChangeSummary"->'touched',:block)) AS "Touched"
                  FROM revisions GROUP BY "GroupId"
                ), changed AS (
                  SELECT h."Version",s->>'from' AS "From",s->>'to' AS "To",coalesce(t."AuthorId",h."AuthorId") AS "AuthorId",
                    coalesce(t."ActiveFrom",h."ActiveFrom") AS "ChangedAt"
                  FROM groups g JOIN revisions h ON h."Version"=g."Head" LEFT JOIN revisions t ON t."Version"=g."Touched"
                  CROSS JOIN LATERAL jsonb_array_elements(h."ChangeSummary"->'states') s
                  WHERE s->>'blockId'=:block
                )
                SELECT c."From",c."To",c."AuthorId",coalesce(a."DisplayName",'') AS "ChangedBy",c."ChangedAt"
                FROM changed c LEFT JOIN "UserAccount" a ON a."Id"=c."AuthorId"
                ORDER BY c."Version" DESC
                LIMIT :limit
                """)
            .AddScalar("From", NHibernateUtil.String).AddScalar("To", NHibernateUtil.String).AddScalar("AuthorId", NHibernateUtil.Guid)
            .AddScalar("ChangedBy", NHibernateUtil.String).AddScalar("ChangedAt", NHibernateUtil.UtcDateTime)
            .SetParameter("page", pageId).SetParameter("block", blockId).SetParameter("version", version)
            .SetParameter("limit", limit ?? int.MaxValue).ListAsync<object[]>(ct);
        return rows.Select(row => ((string)row[0], (string)row[1], (Guid)row[2], (string)row[3], (DateTime)row[4])).ToArray();
    }

    /// <summary>The rows with each block's section (<see cref="Requirement.Section"/>), read from the revisions they come from in one
    /// statement: the index keeps no headings.</summary>
    // shortcut: parses each shown page's content per read; index the section path with the blocks if large spaces make the overview slow.
    public async Task<IReadOnlyList<RequirementRow>> WithSectionsAsync(IReadOnlyList<RequirementRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return rows;
        }
        var contents = await session.QueryOver<PageRevision>().WhereRestrictionOn(x => x.Id).IsIn(rows.Select(row => row.RevisionId).Distinct().Cast<object>().ToArray())
            .Select(x => x.Id, x => x.ContentJson).ListAsync<object[]>(ct);
        var sections = contents.ToDictionary(row => (Guid)row[0], row => Sections((string)row[1]));
        return rows.Select(row => row with
        {
            Requirement = row.Requirement with { Section = sections.GetValueOrDefault(row.RevisionId)?.GetValueOrDefault(row.Requirement.BlockId).Section ?? [] }
        }).ToArray();
    }

    /// <summary>Each block of a revision's content with its section, by block id (unique on a saved page).</summary>
    public static IReadOnlyDictionary<string, (JsonNode Block, IReadOnlyList<string> Section)> Sections(string contentJson)
        => Sections(JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!["document"]!);

    public static IReadOnlyDictionary<string, (JsonNode Block, IReadOnlyList<string> Section)> Sections(JsonNode document)
    {
        return RequirementText.Sectioned(document)
            .GroupBy(entry => RequirementText.Value(entry.Block, "id"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (group.First().Block, group.First().Section), StringComparer.Ordinal);
    }

    private static ISQLQuery Scalars(ISQLQuery query)
    {
        foreach (var column in new[] { "BlockId", "Key", "Title", "Direction", "State", "Date", "Party", "Where", "Requires", "ParentBlockId" })
        {
            query.AddScalar(column, NHibernateUtil.String);
        }
        return query.AddScalar("Position", NHibernateUtil.Int32).AddScalar("PartyContactId", NHibernateUtil.String);
    }

    private static Requirement Read(object[] row, int at)
    {
        return new Requirement((string)row[at], (string)row[at + 1], (string)row[at + 2], (string)row[at + 3], (string)row[at + 4], (string)row[at + 5],
            (string)row[at + 6], (string)row[at + 7], RequirementAttributes.ParseRequires((string)row[at + 8]), (string?)row[at + 9], (int)row[at + 10])
        {
            PartyContactId = (string)row[at + 11]
        };
    }
}
