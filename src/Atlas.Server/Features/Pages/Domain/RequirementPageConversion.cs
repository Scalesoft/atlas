using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Events;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Domain;

/// <summary>Revisions an open need not try to convert again (<see cref="RequirementPageConversion.RepairOnOpenAsync"/>): refused by the gate
/// or with nothing current to convert, for good; with live edits, for a while. Per process: another server instance learns it by its own
/// first try.</summary>
public sealed class RequirementRepairMemory
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> m_skipped = new();

    public bool Skips(Guid revisionId) => m_skipped.TryGetValue(revisionId, out var until) && until > DateTime.UtcNow;

    public void Skip(Guid revisionId, TimeSpan? duration) => m_skipped[revisionId] = duration is { } span ? DateTime.UtcNow + span : DateTime.MaxValue;
}

/// <summary>What <see cref="RequirementPageConversion.ConvertPagesAsync"/> did: a report per revision read, the pages converted (in a dry run:
/// that would be), the pages left for their unsaved live edits, the revisions rewritten (would be) and the revisions left because their
/// page failed the gate.</summary>
public sealed record RequirementPagesConversion(IReadOnlyList<RequirementConversionReport> Reports, IReadOnlySet<Guid> Converted, IReadOnlySet<Guid> Deferred,
    int Revisions, int Refused);

/// <summary>
/// The 3.6 conversion of a page's requirement blocks to requirement headings (model §10b), and the move of a version-3 page's party into
/// its headings' attributes (shape §6, <see cref="MoveMarker"/>), one function for the job's batches
/// (<see cref="ConvertRequirementsHeadingsHandler"/>) and for repair on open (<see cref="RepairOnOpenAsync"/>). The working and published
/// revisions of each page, read again here under the caller's page locks, are converted by <see cref="RequirementHeadingConverter"/> with
/// the global key map; a page converts whole only when every revision validates, reads back from its Markdown, loses and gains nothing a
/// reported case does not explain, and its cases fit what is left of the owner's approved budget (<see cref="ApprovedCases"/>, used
/// cumulatively by the job and every open in <c>RequirementConversionUse</c>). Its originals go to <c>RequirementConversionBackup</c>
/// first, its revisions are rewritten in place, what is derived from them is refreshed (the published search document and page links here,
/// the working search, mentions and requirement index by their triggers, the entity catalog and content references by
/// <c>ContentRebuilt</c>, which tells nobody) and its live collaboration is reset. A page with unsaved live edits or an open editor, and a
/// page an administrator rolled back, is left as stored.
/// Locks: the caller holds the content lock, <see cref="LockKey"/> and its pages' row locks. The job takes the key first and waits for it
/// (then its pages FOR UPDATE); an open holds its page first and only tries the key (<see cref="RepairOnOpenAsync"/>), serving the page as
/// stored when a conversion is running. So conversions run one at a time and use the budget in turn, and no open ever waits for the key: a
/// transaction that holds it never waits for one that waits for it.
/// </summary>
public sealed class RequirementPageConversion(ISession session, IAtlasLocks locks, Outbox outbox, RequirementRepairMemory memory,
    ILogger<RequirementPageConversion> logger)
{
    /// <summary>The stored JSON of a revision that still holds a requirement block (stored JSON is compact).</summary>
    public const string Marker = "\"type\":\"requirement\"";

    /// <summary>The stored JSON of a revision of version 3 (shape §6): its party is still in the facts lines.</summary>
    public const string MoveMarker = "{\"id\":\"atlas-requirements\",\"version\":3}";

    /// <summary>A stored revision still in an old shape: a requirement block or version 3 (<see cref="Marker"/>, <see cref="MoveMarker"/>).</summary>
    public static bool Holds(string json) => json.Contains(Marker, StringComparison.Ordinal) || json.Contains(MoveMarker, StringComparison.Ordinal);

    /// <summary><see cref="Holds"/> of a revision <c>r</c> in SQL.</summary>
    public const string OldShape = """(strpos(r."ContentJson", '"type":"requirement"') > 0 OR strpos(r."ContentJson", '{"id":"atlas-requirements","version":3}') > 0)""";

    public const string LockKey = "atlas.requirements-headings";

    /// <summary>
    /// The reported cases approved from the dry run on a copy of a production database (2026-11-04): the nested block becomes a plain
    /// heading with its children,
    /// two requirements without a title and a key keep their old ids, seven loose blocks go under the requirement before them. A budget
    /// per kind over the job and every repair on open: a case beyond it, or of any other kind, refuses its page.
    /// </summary>
    public static readonly IReadOnlyDictionary<RequirementCaseKind, int> ApprovedCases = new Dictionary<RequirementCaseKind, int>
    {
        [RequirementCaseKind.Nested] = 1, [RequirementCaseKind.IdFallback] = 2, [RequirementCaseKind.EmptyTitle] = 2, [RequirementCaseKind.LooseContent] = 7,
    };

    /// <summary>A page's cases by kind, each kind counted once over its revisions (the larger count): the working and the published revision
    /// report the same block's case twice.</summary>
    public static IReadOnlyDictionary<RequirementCaseKind, int> PageCases(IEnumerable<RequirementConversionReport> revisions)
        => revisions.SelectMany(report => report.Cases.GroupBy(x => x.Kind).Select(x => (Kind: x.Key, Count: x.Count())))
            .GroupBy(x => x.Kind).ToDictionary(x => x.Key, x => x.Max(y => y.Count));

    /// <summary>Why a page's cases do not fit what is left of <see cref="ApprovedCases"/> after <paramref name="used"/>, or null: then they are
    /// added to <paramref name="used"/>.</summary>
    public static string? Admit(IReadOnlyDictionary<RequirementCaseKind, int> cases, Dictionary<string, int> used)
    {
        foreach (var (kind, count) in cases)
        {
            if (!ApprovedCases.TryGetValue(kind, out var budget))
            {
                return $"case {kind} is not approved";
            }
            if (used.GetValueOrDefault(kind.ToString()) + count > budget)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{count} {kind} beyond the approved {budget} ({used.GetValueOrDefault(kind.ToString())} used)");
            }
        }
        foreach (var (kind, count) in cases)
        {
            used[kind.ToString()] = used.GetValueOrDefault(kind.ToString()) + count;
        }
        return null;
    }

    /// <summary>The budget used so far by kind (<c>RequirementConversionUse</c>).</summary>
    public async Task<Dictionary<string, int>> UseAsync(CancellationToken ct)
        => (await session.CreateSQLQuery("""SELECT "Kind", "Used" FROM "RequirementConversionUse" """)
            .AddScalar("Kind", NHibernateUtil.String).AddScalar("Used", NHibernateUtil.Int32).ListAsync<object[]>(ct))
            .ToDictionary(row => (string)row[0], row => (int)row[1], StringComparer.Ordinal);

    /// <summary>
    /// Repair on open: when the content a read loaded (<paramref name="loaded"/>, revision <paramref name="revisionId"/> of
    /// <paramref name="pageId"/>) is still in an old shape (<see cref="Holds"/>), the page is converted first, under its row lock FOR SHARE (a save waits,
    /// a read does not). True when the caller must read the revision again. Without the marker it costs nothing, not even a query; a revision
    /// known not to convert now (refused by the gate, no current revision to convert, as a share pinned to an old revision; for a while: live
    /// edits) costs a look into <see cref="RequirementRepairMemory"/>; a page rolled back or with live edits costs one indexed lookup, before
    /// any lock. A page left as stored is logged and served as it is, also while another conversion holds <see cref="LockKey"/>.
    /// </summary>
    public async Task<bool> RepairOnOpenAsync(Guid pageId, Guid revisionId, string? loaded, CancellationToken ct)
    {
        if (loaded is null || !Holds(loaded) || memory.Skips(revisionId))
        {
            return false;
        }
        var state = await session.CreateSQLQuery("""
                SELECT EXISTS (SELECT 1 FROM "RequirementConversionBackup" b WHERE b."PageId" = :page AND b."RolledBack") AS "RolledBack",
                  EXISTS (SELECT 1 FROM "PageCollaboration" c WHERE c."PageId" = :page
                    AND c."Sequence" > coalesce(c."LastSavedSequence", 0)) AS "Live"
                """).AddScalar("RolledBack", NHibernateUtil.Boolean).AddScalar("Live", NHibernateUtil.Boolean).SetParameter("page", pageId)
            .UniqueResultAsync<object[]>(ct);
        if ((bool)state[0] || (bool)state[1])
        {
            // A live page waits for its editors (the job comes back for it); a rolled-back one stays.
            memory.Skip(revisionId, (bool)state[0] ? null : ConvertRequirementsOverviewHandler.PassDelay);
            logger.LogInformation("Page {PageId} still holds requirement blocks: served as stored ({Reason}).", pageId, (bool)state[0] ? "rolled back" : "live edits first");
            return false;
        }
        await locks.AcquireContentSharedAsync(ct);
        if (!await locks.LockPageSharedAsync(pageId, ct))
        {
            return true;
        }
        if (!await session.CreateSQLQuery("""SELECT pg_try_advisory_xact_lock(hashtextextended(:key, 812)) AS "Held" """).AddScalar("Held", NHibernateUtil.Boolean)
                .SetParameter("key", LockKey).UniqueResultAsync<bool>(ct))
        {
            logger.LogInformation("Page {PageId} still holds requirement blocks: served as stored while another conversion runs.", pageId);
            return false;
        }
        var result = await ConvertPagesAsync([pageId], null, ct);
        if (result.Converted.Contains(pageId))
        {
            logger.LogInformation("Page {PageId} converted to requirement headings on open ({Revisions} revisions).", pageId, result.Revisions);
        }
        else if (result.Deferred.Contains(pageId))
        {
            memory.Skip(revisionId, ConvertRequirementsOverviewHandler.PassDelay);
            logger.LogInformation("Page {PageId} still holds requirement blocks: served as stored, its live edits come first (the job converts it later).", pageId);
        }
        else
        {
            // Refused by the gate, or nothing current to convert (the loaded revision is an old one): the same revision never converts.
            memory.Skip(revisionId, null);
            if (result.Refused > 0)
            {
                logger.LogWarning("Page {PageId} still holds requirement blocks: served as stored, the conversion's gate refused it.", pageId);
            }
        }
        return true;
    }

    /// <summary>The conversion of <paramref name="pages"/> (see the class). <paramref name="dryRunUse"/> makes it a dry run: nothing is written,
    /// nobody's live state is asked, and the budget is used in that dictionary only.</summary>
    public async Task<RequirementPagesConversion> ConvertPagesAsync(IReadOnlyList<Guid> pages, Dictionary<string, int>? dryRunUse, CancellationToken ct)
    {
        var dryRun = dryRunUse is not null;
        // Read under the locks: what a save or another conversion committed meanwhile is what is converted.
        var revisions = (await session.CreateSQLQuery($"""
                SELECT r."Id", r."PageId", r."ContentJson", r."ActiveTo" IS NULL AS "Working", p."SpaceId" FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
                WHERE r."PageId" = {GuidList.Any(":ids")} AND {Current}
                  AND NOT EXISTS (SELECT 1 FROM "RequirementConversionBackup" b WHERE b."PageId" = r."PageId" AND b."RolledBack")
                ORDER BY r."PageId", r."Id"
                """).AddScalar("Id", NHibernateUtil.Guid).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("ContentJson", NHibernateUtil.String)
            .AddScalar("Working", NHibernateUtil.Boolean).AddScalar("SpaceId", NHibernateUtil.Guid).SetParameter("ids", GuidList.Param(pages)).ListAsync<object[]>(ct))
            .Select(row => (Id: (Guid)row[0], PageId: (Guid)row[1], Json: (string)row[2], Working: (bool)row[3], Space: (Guid)row[4])).ToList();
        if (!revisions.Any(x => Holds(x.Json)))
        {
            return new RequirementPagesConversion([], new HashSet<Guid>(), new HashSet<Guid>(), 0, 0);
        }
        var catalogs = await CatalogsAsync(ct);
        var map = await KeyMapAsync(revisions.Select(x => (x.PageId, x.Working, x.Json)),
            revisions.Select(x => x.Space).Distinct().SelectMany(space => catalogs.GetValueOrDefault(space) ?? []), ct);
        var reports = new List<RequirementConversionReport>();
        var due = new List<(Guid Id, Guid PageId, string Json, string Markdown)>();
        var held = new HashSet<Guid>();
        var refused = 0;
        foreach (var (id, pageId, stored, _, space) in revisions)
        {
            try
            {
                // The content as every reader reads it (LegacyDocumentRepair: a 3.6 disclosure is a heading), else the schema check refuses the page.
                var conversion = RequirementHeadingConverter.Convert(LegacyDocumentRepair.Repair(stored), pageId, map, catalogs.GetValueOrDefault(space));
                reports.Add(conversion.Report);
                if (conversion.Report.Cases.Any(x => x.Kind == RequirementCaseKind.Unreadable) || conversion.Changed && !conversion.Report.Passes)
                {
                    logger.LogWarning("Revision {RevisionId} of page {PageId} keeps its stored shape: {Losses} losses and {Gains} gains no reported case explains.",
                        id, pageId, conversion.Report.Losses.Count(x => x.Accepted is null), conversion.Report.Gains.Count(x => x.Accepted is null));
                    held.Add(pageId);
                    refused++;
                }
                else if (conversion.Changed)
                {
                    due.Add((id, pageId, conversion.Json, Markdown(conversion.Json)));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning("Revision {RevisionId} of page {PageId} keeps its stored shape: {Message}", id, pageId, error.Message);
                held.Add(pageId);
                refused++;
            }
        }
        // A page converts whole or not at all: its working and published revisions keep one shape.
        due.RemoveAll(x => held.Contains(x.PageId));
        var editing = dryRun ? new HashSet<Guid>() : await ConvertRequirementsOverviewHandler.EditingAsync(session, due.Select(x => x.PageId).Distinct().ToList(), ct, recentCounts: false);
        // The approved budget, used by the pages that convert now, in page order.
        var used = dryRunUse ?? await UseAsync(ct);
        var before = new Dictionary<string, int>(used, StringComparer.Ordinal);
        foreach (var pageId in due.Select(x => x.PageId).Distinct().Where(x => !editing.Contains(x)).Order().ToList())
        {
            if (Admit(PageCases(reports.Where(x => x.PageId == pageId)), used) is { } reason)
            {
                logger.LogWarning("Page {PageId} keeps its stored shape: {Reason}", pageId, reason);
                refused += due.RemoveAll(x => x.PageId == pageId);
            }
        }
        var converted = new HashSet<Guid>();
        var written = 0;
        foreach (var (id, pageId, json, markdown) in due.Where(x => !editing.Contains(x.PageId)))
        {
            written++;
            converted.Add(pageId);
            if (dryRun)
            {
                continue;
            }
            await RewriteAsync(session, id, pageId, json, markdown, ct);
        }
        if (!dryRun && converted.Count > 0)
        {
            // An in-place update announces nothing by itself: the search documents are refreshed here. The requirement index and the page
            // links are not: the pages' marks went with the rewrite, so a read refreshes the index, and the outbox's delivery of the
            // announcement below rebuilds both (ProjectorIndex, PageLinkProjection).
            await Search.SearchDocuments.RefreshAsync(session, converted, ct);
            foreach (var pageId in converted)
            {
                // Nothing new to tell anyone: the entity catalog and the content references rebuild the page from its current state.
                outbox.ContentRebuilt(pageId, PagesAtlasModule.ContentType);
            }
            await new CollaborationCheckpoints(session).ResetAllAsync(converted, ct);
            foreach (var (kind, count) in used.Where(x => before.GetValueOrDefault(x.Key) != x.Value))
            {
                await session.CreateSQLQuery("""
                        INSERT INTO "RequirementConversionUse"("Kind","Used") VALUES (:kind, :used) ON CONFLICT ("Kind") DO UPDATE SET "Used" = EXCLUDED."Used"
                        """).SetParameter("kind", kind).SetParameter("used", count).ExecuteUpdateAsync(ct);
            }
        }
        return new RequirementPagesConversion(reports, converted, editing, written, refused);
    }

    /// <summary>The one rewrite in place of a current revision by a 3.6 conversion (this one, pages.requirements-overview), under the page's
    /// row lock: the revision as it was before its first rewrite goes to <c>RequirementConversionBackup</c> (a later rewrite, by either job
    /// in either order, keeps that row), and the page's index marks go, so a read refreshes its requirement index.</summary>
    /// <param name="backup">False for a rewrite that is no conversion (pages.requirement-systems): the backup holds the originals of the 3.6
    /// conversion and tells which pages it converted (<see cref="KeyMapAsync"/>), so a page it never touched gets no row.</param>
    public static Task RewriteAsync(ISession session, Guid revisionId, Guid pageId, string json, string markdown, CancellationToken ct, bool backup = true)
        => session.CreateSQLQuery("""
                INSERT INTO "RequirementConversionBackup"("RevisionId","PageId","ContentJson","Markdown")
                SELECT "Id","PageId","ContentJson","Markdown" FROM "PageRevision" WHERE "Id" = :id AND :backup ON CONFLICT DO NOTHING;
                UPDATE "PageRevision" SET "ContentJson" = :json, "Markdown" = :markdown WHERE "Id" = :id;
                DELETE FROM "ProjectorIndexedRevision" WHERE "PageId" = :page
                """).SetParameter("backup", backup).SetParameter("json", json).SetParameter("markdown", markdown).SetParameter("id", revisionId).SetParameter("page", pageId).ExecuteUpdateAsync(ct);

    /// <summary>
    /// A write of content (create, a save outside a live session: the web, PUT, MCP; the page source; a restore of history) in an old shape
    /// (<see cref="Holds"/>) is converted by the same function as the job and repair on open, with the key map of the page's space, so no
    /// write stores the old shape. <paramref name="pageId"/> is <see cref="RequirementHeadings.NewPage"/> for a page not created yet. Content
    /// the conversion cannot carry without a loss no reported case explains is refused (400), never stored as it came; unreadable JSON is
    /// returned as it is, for the validation to refuse. No budget: the approved cases are counted for the stored pages only. The old shape
    /// is read from the parsed JSON (<see cref="RequirementHeadingConverter.OldShape"/>), whatever its spacing.
    /// </summary>
    /// <param name="refusal">The message of the refusal; the default speaks of written content.</param>
    public async Task<string> ConvertWriteAsync(Guid pageId, Guid spaceId, string json, CancellationToken ct, string? refusal = null)
    {
        if (!RequirementHeadingConverter.OldShape(json))
        {
            return json;
        }
        var catalog = (await CatalogsAsync(ct)).GetValueOrDefault(spaceId) ?? [];
        var map = await KeyMapAsync([(pageId, true, json)], catalog, ct);
        RequirementConversion conversion;
        try
        {
            conversion = RequirementHeadingConverter.Convert(json, pageId, map, catalog);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("A write to page {PageId} in the old requirement shape does not convert: {Message}", pageId, error.Message);
            throw Refused(refusal);
        }
        if (conversion.Report.Cases.Any(x => x.Kind == RequirementCaseKind.Unreadable))
        {
            return json;
        }
        return !conversion.Changed || conversion.Report.Passes ? conversion.Json : throw Refused(refusal);
    }

    /// <summary>The refusal of a write <see cref="ConvertWriteAsync"/> cannot convert.</summary>
    public const string ShapeRefusal = "Obsah má požadavky v původním tvaru (bloky požadavků), které nejde beze ztráty převést na nadpisy požadavků. " +
        "Zapište požadavky jako nadpisy (### Název {#id state=planned}).";

    private static ApiException Refused(string? message) => new(400, message ?? ShapeRefusal, "requirement_shape");

    /// <summary>The catalog pages of each space in catalog order, as ProjectorCatalogReader orders them (without its per-actor read rule:
    /// provenance is the same for every reader); a conversion's map reads those of its pages' spaces, for the blocks' `from`.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<Guid>>> CatalogsAsync(CancellationToken ct)
        => (await session.CreateSQLQuery("""
                SELECT c."PageId", p."SpaceId" FROM "ProjectorCatalogPage" c JOIN "Page" p ON p."Id" = c."PageId"
                WHERE p."PurgedAt" IS NULL AND NOT p."IsTrashed" ORDER BY c."MarkedAt", c."PageId"
                """).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("SpaceId", NHibernateUtil.Guid).ListAsync<object[]>(ct))
            .GroupBy(row => (Guid)row[1], row => (Guid)row[0]).ToDictionary(x => x.Key, x => (IReadOnlyList<Guid>)x.ToList());

    /// <summary>
    /// The key map of a conversion: the pages' own revisions as read under the locks, and the pages their blocks depend on (<c>key@pageId</c>)
    /// and <paramref name="catalog"/> (the catalog pages of their spaces): those still in the old shape from <c>ProjectorRequirementIndex</c>
    /// (made fresh first), those converted already (rows in the backup) from their headings' ids and aliases. Only those pages; the map
    /// knows which it read (<see cref="RequirementKeyMap.HasRead"/>), so a missing one is a loss the gate sees, never an empty `from`.
    /// </summary>
    private async Task<RequirementKeyMap> KeyMapAsync(IEnumerable<(Guid PageId, bool Working, string Json)> own, IEnumerable<Guid> catalog, CancellationToken ct)
    {
        var rows = new List<RequirementKeyRow>();
        var list = own.ToList();
        var referenced = new HashSet<Guid>();
        foreach (var (pageId, working, json) in list)
        {
            if (Document(json) is { } document)
            {
                rows.AddRange(RequirementKeyMap.Rows(pageId, working, document));
                rows.AddRange(RequirementKeyMap.ConvertedRows(pageId, working, document));
                referenced.UnionWith(RequirementHeadingConverter.Blocks(document)
                    .SelectMany(block => Projector.Domain.RequirementAttributes.ParseRequires(RequirementText.Value(block, "requires")))
                    .Select(reference => Projector.Domain.RequirementAttributes.CrossPage(reference)?.PageId).OfType<Guid>());
            }
        }
        referenced.UnionWith(catalog);
        referenced.ExceptWith(list.Select(x => x.PageId));
        var read = referenced.Concat(list.Select(x => x.PageId)).ToHashSet();
        if (referenced.Count == 0)
        {
            return RequirementKeyMap.Build(rows, read);
        }
        await Projector.Persistence.ProjectorIndex.EnsureFreshAsync(session, referenced, ct);
        var ids = GuidList.Param(referenced);
        rows.AddRange((await session.CreateSQLQuery($"""
                SELECT i."PageId", i."IsWorking", i."BlockId", i."Key", i."Title" FROM "ProjectorRequirementIndex" i JOIN "PageRevision" r ON r."Id" = i."RevisionId"
                WHERE i."PageId" = {GuidList.Any(":ids")} AND NOT ({Converted})
                ORDER BY i."PageId", i."Position"
                """).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("IsWorking", NHibernateUtil.Boolean).AddScalar("BlockId", NHibernateUtil.String)
            .AddScalar("Key", NHibernateUtil.String).AddScalar("Title", NHibernateUtil.String).SetParameter("ids", ids).ListAsync<object[]>(ct))
            .Select(row => new RequirementKeyRow((Guid)row[0], (bool)row[1], (string)row[2], (string)row[3], (string)row[4])));
        foreach (var row in await session.CreateSQLQuery($"""
                SELECT r."PageId", r."ActiveTo" IS NULL AS "Working", r."ContentJson" FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
                WHERE {Current} AND r."PageId" = {GuidList.Any(":ids")} AND {Converted}
                """).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("Working", NHibernateUtil.Boolean).AddScalar("ContentJson", NHibernateUtil.String)
            .SetParameter("ids", ids).ListAsync<object[]>(ct))
        {
            if (Document((string)row[2]) is { } document)
            {
                rows.AddRange(RequirementKeyMap.ConvertedRows((Guid)row[0], (bool)row[1], document));
            }
        }
        return RequirementKeyMap.Build(rows, read);
    }

    private static JsonNode? Document(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions)?["document"];
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The Markdown of a converted revision that the editor schema (atlas-requirements 3, heading-anchors 2) accepts and that
    /// reads back from that Markdown as it is (<see cref="RequirementHeadingConverter.RoundTripFailure"/>); otherwise it throws and the
    /// page keeps its stored shape.</summary>
    private static string Markdown(string json)
    {
        var markdown = Rules.ValidateDocument(json).Markdown;
        return RequirementHeadingConverter.RoundTripFailure(json) is { } failure ? throw new InvalidOperationException("Markdown round trip differs at " + failure) : markdown;
    }

    /// <summary>A revision <c>r</c> this conversion has done: no requirement block left, on a page with a backup not rolled back. A backup
    /// alone does not tell: pages.requirements-overview backs up its rewrites too, of pages still holding blocks.</summary>
    private const string Converted = """
        strpos(r."ContentJson", '"type":"requirement"') = 0 AND EXISTS (SELECT 1 FROM "RequirementConversionBackup" b WHERE b."PageId" = r."PageId" AND NOT b."RolledBack")
        """;

    /// <summary>A working or published revision of a page not purged (in the trash too, a template too).</summary>
    public const string Current = """p."PurgedAt" IS NULL AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId")""";
}
