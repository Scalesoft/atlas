using NHibernate;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector.Persistence;

/// <summary>
/// Dependencies between pages (plan 3.1 §2.3 point 5, <c>key@pageId</c>): the blocks of other pages a reader may read, with the rule of
/// the space reads (<see cref="RequirementIndex.SpaceAsync"/>: an untrashed, non-template page with a page role, an unpublished one only
/// for its editing owner or a WikiAdmin; the working rows for an editor, the published ones otherwise), decided in SQL for every page at
/// once, in any space and without a page lock. What the reader may not read is not told: it is unknown. On MCP and for the assistant a
/// page of a space switched out of AI is unreadable (G5, <see cref="AccessSql.SpaceInAi"/>, as RelationNodes).
/// </summary>
public sealed class CrossPageRequirements(ISession session, IRequestChannel channel)
{
    /// <summary>How far a cycle is looked for across pages; a longer chain is let through.</summary>
    public const int MaxCycleSteps = 2_000;

    private static readonly string Visible = $$"""
        visible AS MATERIALIZED (
          SELECT p."Id",(NOT :published AND (a."IsWikiAdmin" OR coalesce(e."Role",0)>=2
            OR p."PublishedRevisionId" IS NOT NULL AND coalesce(p."PublicationMode",s."PublicationMode")='Live')) AS "Working"
          FROM "Page" p JOIN "Space" s ON s."Id"=p."SpaceId" CROSS JOIN actor a LEFT JOIN "EffectivePageAccess" e ON e."PageId"=p."Id" AND e."UserId"=:actor
          WHERE p."Id" IN (SELECT "Id" FROM wanted) AND NOT p."IsTrashed" AND NOT p."IsTemplate" AND (a."IsWikiAdmin" OR e."Role">=1)
            AND (p."PublishedRevisionId" IS NOT NULL OR NOT :published AND (a."IsWikiAdmin" OR e."Role">=2) AND (p."OwnerId"=a."Id" OR a."IsWikiAdmin"))
            AND (NOT :ai OR {{AccessSql.SpaceInAi("p.\"SpaceId\"")}})
        )
        """;

    private const string Columns = """
        i."PageId",r."Title" AS "PageTitle",i."BlockId",i."Key",i."Title",i."Direction",i."State",i."Date",i."Party",i."Where",i."Requires",
        i."ParentBlockId",i."Position",i."PartyContactId"
        """;

    /// <summary>The prerequisites <paramref name="references"/> name (<c>key@pageId</c>; anything else is left out), each as the actor
    /// reads it, by reference.</summary>
    public async Task<IReadOnlyDictionary<string, RequirementPrerequisite>> PrerequisitesAsync(UserAccount actor, IEnumerable<string> references,
        CancellationToken ct)
    {
        var wanted = references.Distinct(StringComparer.Ordinal).Select(x => (Reference: x, Target: RequirementAttributes.CrossPage(x)))
            .Where(x => x.Target is not null).ToArray();
        if (wanted.Length == 0)
        {
            return new Dictionary<string, RequirementPrerequisite>();
        }
        var rows = await PagesAsync(actor, wanted.Select(x => x.Target!.Value.PageId), ct);
        var byKey = rows.GroupBy(row => (row.PageId, row.Requirement.Key)).ToDictionary(group => group.Key, group => group.First());
        return wanted.ToDictionary(x => x.Reference, x =>
        {
            var (key, page) = x.Target!.Value;
            return byKey.TryGetValue((page, key), out var row)
                ? new RequirementPrerequisite(x.Reference, page, key, true)
                {
                    PageTitle = row.PageTitle, BlockId = row.Requirement.BlockId, Title = row.Requirement.Title, State = row.Requirement.State,
                    Direction = row.Requirement.Direction
                }
                : new RequirementPrerequisite(x.Reference, page, key, false);
        }, StringComparer.Ordinal);
    }

    /// <summary>Every block of <paramref name="pageIds"/> the actor may read, as the space reads give them (no section, no page counts).</summary>
    public async Task<IReadOnlyList<RequirementRow>> PagesAsync(UserAccount actor, IEnumerable<Guid> pageIds, CancellationToken ct)
    {
        var ids = pageIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        await ProjectorIndex.EnsureFreshAsync(session, ids, ct);
        return await ReadAsync(actor, """
            wanted AS (SELECT x AS "Id" FROM unnest(CAST(string_to_array(:ids, ',') AS uuid[])) x)
            """, "TRUE", query => query.SetParameter("ids", string.Join(',', ids)), ct);
    }

    /// <summary>„Co to blokuje“: the blocks the actor may read that require <paramref name="key"/> of <paramref name="pageId"/> from
    /// another page (<c>key@pageId</c> of a block, <c>pageId#headingId</c> of a requirement heading), in the order of their pages' titles.</summary>
    public async Task<IReadOnlyList<RequirementRow>> DependentsAsync(UserAccount actor, Guid pageId, string key, CancellationToken ct)
    {
        // The candidates first (strpos on the raw lists of the cross-page rows, their partial indexes of migrations 2026103196 and 2026110820),
        // so the access is decided for those pages only. Any page may depend on it: every page's index is made fresh first.
        await ProjectorIndex.EnsureFreshAsync(session, null, ct);
        return await ReadAsync(actor, """
            wanted AS (SELECT DISTINCT "PageId" AS "Id" FROM "ProjectorRequirementIndex"
              WHERE ("Requires" LIKE '%@%' AND strpos("Requires",:reference)>0 OR "Requires" LIKE '%#%' AND strpos("Requires",:heading)>0) AND "PageId"<>:page)
            """, """string_to_array(i."Requires",',') && ARRAY[CAST(:reference AS text),CAST(:heading AS text)]""",
            query => query.SetParameter("reference", RequirementAttributes.Reference(key, pageId))
                .SetParameter("heading", Platform.Content.RequirementHeadings.Reference(pageId, key)).SetParameter("page", pageId), ct);
    }

    /// <summary>
    /// The cycle that block <paramref name="key"/> of <paramref name="pageId"/> requiring <paramref name="requires"/> would close, walked
    /// over the blocks the actor may read: <paramref name="local"/> gives the requires of the page's own blocks (the draft being saved),
    /// other pages are read as they are. The path from the block back to it, references relative to <paramref name="pageId"/>; null when
    /// there is none.
    /// </summary>
    public async Task<IReadOnlyList<string>?> CycleAsync(UserAccount actor, Guid pageId, string key, IReadOnlyList<string> requires,
        IReadOnlyDictionary<string, IReadOnlyList<string>> local, CancellationToken ct)
    {
        if (key.Length == 0 || !requires.Any(x => RequirementAttributes.CrossPage(x) is not null))
        {
            // Within the page the editor and the catalog keep requires acyclic; only a way through another page is looked for here.
            return null;
        }
        var start = (Page: pageId, Key: key);
        var known = new Dictionary<Guid, IReadOnlyDictionary<string, IReadOnlyList<string>>> { [pageId] = local };
        var parent = new Dictionary<(Guid Page, string Key), (Guid Page, string Key)>();
        var frontier = new List<(Guid Page, string Key)>();
        foreach (var next in requires.Select(x => Target(pageId, x)).Distinct())
        {
            if (next == start)
            {
                return Path(pageId, [start, start]);
            }
            if (parent.TryAdd(next, start))
            {
                frontier.Add(next);
            }
        }
        while (frontier.Count > 0 && parent.Count < MaxCycleSteps)
        {
            var missing = frontier.Select(x => x.Page).Where(page => !known.ContainsKey(page)).Distinct().ToArray();
            foreach (var group in (await PagesAsync(actor, missing, ct)).GroupBy(row => row.PageId))
            {
                known[group.Key] = group.GroupBy(row => row.Requirement.Key, StringComparer.Ordinal)
                    .ToDictionary(keyed => keyed.Key, keyed => keyed.First().Requirement.Requires, StringComparer.Ordinal);
            }
            foreach (var page in missing.Where(page => !known.ContainsKey(page)))
            {
                known[page] = new Dictionary<string, IReadOnlyList<string>>();
            }
            var current = frontier;
            frontier = [];
            foreach (var node in current)
            {
                foreach (var next in known[node.Page].GetValueOrDefault(node.Key, []).Select(x => Target(node.Page, x)).Distinct())
                {
                    if (next == start)
                    {
                        var path = new List<(Guid Page, string Key)> { node, start };
                        for (var at = node; at != start;)
                        {
                            at = parent[at];
                            path.Insert(0, at);
                        }
                        return Path(pageId, path);
                    }
                    if (parent.TryAdd(next, node))
                    {
                        frontier.Add(next);
                    }
                }
            }
        }
        return null;
    }

    private static (Guid Page, string Key) Target(Guid page, string reference)
        => RequirementAttributes.CrossPage(reference) is { } target ? (target.PageId, target.Key) : (page, reference);

    private static string[] Path(Guid pageId, IEnumerable<(Guid Page, string Key)> nodes)
        => nodes.Select(x => x.Page == pageId ? x.Key : RequirementAttributes.Reference(x.Key, x.Page)).ToArray();

    private async Task<IReadOnlyList<RequirementRow>> ReadAsync(UserAccount actor, string wanted, string kept, Func<ISQLQuery, IQuery> bind,
        CancellationToken ct)
    {
        var query = session.CreateSQLQuery($"""
                WITH {AccessSql.ActorCte}, {wanted}, {Visible}
                SELECT {Columns} FROM visible v
                JOIN "ProjectorRequirementIndex" i ON i."PageId"=v."Id" AND CASE WHEN v."Working" THEN i."IsWorking" ELSE i."IsPublished" END
                JOIN "PageRevision" r ON r."Id"=i."RevisionId"
                WHERE {kept}
                ORDER BY r."Title",i."PageId",i."Position"
                """)
            .AddScalar("PageId", NHibernateUtil.Guid).AddScalar("PageTitle", NHibernateUtil.String);
        foreach (var column in new[] { "BlockId", "Key", "Title", "Direction", "State", "Date", "Party", "Where", "Requires", "ParentBlockId" })
        {
            query.AddScalar(column, NHibernateUtil.String);
        }
        query.AddScalar("Position", NHibernateUtil.Int32).AddScalar("PartyContactId", NHibernateUtil.String);
        bind(query);
        var rows = await query.SetParameter("actor", actor.Id).SetParameter("stamp", actor.SecurityStamp)
            .SetParameter("ai", channel.Current.IsAiBound).SetParameter("published", ProjectorPublishedReads.Active).ListAsync<object[]>(ct);
        return rows.Select(row => new RequirementRow((Guid)row[0], (string)row[1], new Requirement((string)row[2], (string)row[3], (string)row[4],
            (string)row[5], (string)row[6], (string)row[7], (string)row[8], (string)row[9], RequirementAttributes.ParseRequires((string)row[10]),
            (string?)row[11], (int)row[12]) { PartyContactId = (string)row[13] })).ToArray();
    }
}
