using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Validation;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Commands;

// Maintenance a migration requests and an administrator may run again from Úlohy (atlas_run_job), not a user's command: no MCP tool of its own.

/// <summary>One batch of <see cref="RequirementsOverviewJob"/> over the checkpoint of the run <paramref name="RunId"/>.</summary>
public sealed record ConvertRequirementsOverview(long RunId);

/// <summary>The checkpoint of a run (JobRun.Progress) and its numbers: pages read up to <see cref="AfterPageId"/>, revisions rewritten,
/// pages deferred for their unsaved live edits and revisions left as they were because today's rules refuse them.</summary>
public sealed record RequirementsOverviewProgress(Guid AfterPageId, int Pages, int Revisions, int Deferred, int Refused)
{
    public static readonly RequirementsOverviewProgress Empty = new(Guid.Empty, 0, 0, 0, 0);
}

/// <summary>What a batch read (0: the pass is done) and the run's numbers after it.</summary>
public sealed record RequirementsOverviewRound(int Pages, RequirementsOverviewProgress Progress);

/// <summary>
/// A batch of the 3.4 content conversion (plan R6): the next <see cref="BatchPages"/> pages whose working or published revision is still
/// in the 3.3 shape (<c>atlas-requirements</c> 1 or a <c>requirementSummary</c>) or, since 3.6, holds a disclosure, under their page locks
/// (as a save takes its page), each such revision rewritten in place by the read repair (<see cref="LegacyDocumentRepair"/>:
/// <c>requirementSummary</c> becomes <c>overview</c> of kind <c>requirements</c>, its id kept; a disclosure a heading and its body) with
/// the plugin declaration raised to the current version, validated and with
/// its Markdown written again. In place, so the revision keeps its author and time (nothing looks edited by hand) and older history stays
/// as stored, read through the same repair. The rewrite is the conversion's own (<see cref="RequirementPageConversion.RewriteAsync"/>):
/// the original goes to the backup first, whichever of the two jobs rewrites a revision first, and the page's requirement index is
/// refreshed by the next read. The page's live collaboration is reset to a new generation, so the next session seeds from the
/// converted revision. A page to convert with unsaved live edits that change its content, or with live activity in the last
/// <see cref="PassDelay"/> (an open editor a reset would interrupt), is not touched, and the pass ends with a request for another run
/// <see cref="PassDelay"/> later. The editor converts a 3.3 summary in its live document itself; a disclosure it cannot hold (rich
/// revision 7), so a page with unsaved live edits holding one must be saved before the upgrade (release checklist). A revision whose conversion or validation throws stays as stored,
/// counted as refused, and the other pages go on. The schema generation of a live state (OpenCollaboration, UpgradeKnownSchema)
/// is left to the editor's next join: a reset unbinds the schema, a deferred page keeps it.
/// </summary>
public sealed class ConvertRequirementsOverviewHandler(ISession session, IAtlasLocks locks, IJobRequests jobs, ILogger<ConvertRequirementsOverviewHandler> logger)
{
    public const int BatchPages = 50;
    public static readonly TimeSpan PassDelay = TimeSpan.FromMinutes(10);
    private const string Plugin = "atlas-requirements";

    public async Task<RequirementsOverviewRound> Handle(ConvertRequirementsOverview command, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        var progress = await ProgressAsync(session, command.RunId, ct);
        var batch = (await session.CreateSQLQuery(NextPages).AddScalar("PageId", NHibernateUtil.Guid)
            .SetParameter("after", progress.AfterPageId).SetParameter("pages", BatchPages).ListAsync<Guid>(ct)).ToList();
        if (batch.Count == 0)
        {
            if (progress.Deferred > 0)
            {
                await jobs.RequestAsync(RequirementsOverviewJob.JobId, "Stránky s neuloženými živými úpravami", notBefore: DateTime.UtcNow + PassDelay, ct: ct);
            }
            return new RequirementsOverviewRound(0, progress);
        }
        var pages = await locks.LockPagesForUpdateAsync(batch, ct);
        // Read under the locks: what a save committed meanwhile is what is converted.
        var revisions = await session.CreateSQLQuery($"""
                SELECT r."Id", r."PageId", r."ContentJson" FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
                WHERE r."PageId" = {GuidList.Any(":ids")} AND {Holds} ORDER BY r."PageId", r."Id"
                """).AddScalar("Id", NHibernateUtil.Guid).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("ContentJson", NHibernateUtil.String)
            .SetParameter("ids", GuidList.Param(pages)).ListAsync<object[]>(ct);
        var due = new List<(Guid Id, Guid PageId, string Json, string Markdown)>();
        var refused = 0;
        foreach (var row in revisions)
        {
            var (id, pageId, stored) = ((Guid)row[0], (Guid)row[1], (string)row[2]);
            try
            {
                var json = Convert(stored);
                if (!ReferenceEquals(json, stored))
                {
                    due.Add((id, pageId, json, Rules.ValidateDocument(json).Markdown));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A revision stored under older rules stays as it is, read through the repair, rather than failing every other page.
                logger.LogWarning("Revision {RevisionId} of page {PageId} keeps its stored shape: {Message}", id, pageId, error.Message);
                refused++;
            }
        }
        // Only a page that has a revision to rewrite waits for its editor: one already converted is never deferred.
        var editing = await EditingAsync(session, due.Select(x => x.PageId).Distinct().ToList(), ct);
        var converted = new HashSet<Guid>();
        var written = 0;
        foreach (var (id, pageId, json, markdown) in due.Where(x => !editing.Contains(x.PageId)))
        {
            await RequirementPageConversion.RewriteAsync(session, id, pageId, json, markdown, ct);
            converted.Add(pageId);
            written++;
        }
        if (converted.Count > 0)
        {
            await new CollaborationCheckpoints(session).ResetAllAsync(converted, ct);
            await Search.SearchDocuments.RefreshAsync(session, converted, ct);
        }
        progress = new RequirementsOverviewProgress(batch[^1], progress.Pages + batch.Count, progress.Revisions + written,
            progress.Deferred + editing.Count, progress.Refused + refused);
        await session.CreateSQLQuery("""UPDATE "JobRun" SET "Progress" = CAST(:progress AS jsonb) WHERE "Id" = :id""")
            .SetParameter("progress", JsonSerializer.Serialize(progress, JsonSerializerOptions.Web)).SetParameter("id", command.RunId).ExecuteUpdateAsync(ct);
        return new RequirementsOverviewRound(batch.Count, progress);
    }

    public static async Task<RequirementsOverviewProgress> ProgressAsync(ISession session, long runId, CancellationToken ct)
        => await session.CreateSQLQuery("""SELECT CAST("Progress" AS text) AS "Progress" FROM "JobRun" WHERE "Id" = :id""")
            .AddScalar("Progress", NHibernateUtil.String).SetParameter("id", runId).UniqueResultAsync<string?>(ct) is { } json
            ? JsonSerializer.Deserialize<RequirementsOverviewProgress>(json, JsonSerializerOptions.Web)!
            : RequirementsOverviewProgress.Empty;

    /// <summary>Of <paramref name="pages"/>, those with unsaved live edits that change their content or with live activity in the last
    /// <see cref="PassDelay"/>: a reset would lose or interrupt them (also pages.requirements-headings).</summary>
    internal static async Task<HashSet<Guid>> EditingAsync(ISession session, IReadOnlyCollection<Guid> pages, CancellationToken ct, bool recentCounts = true)
        => (await session.CreateSQLQuery($"""
                SELECT c."PageId", (:recentCounts AND c."UpdatedAt" > :recent) AS "Recent", CASE WHEN c."SnapshotSequence" = c."Sequence" THEN c."ProjectionJson" END AS "Projection",
                    r."ContentJson" AS "Working"
                FROM "PageCollaboration" c LEFT JOIN "PageRevision" r ON r."PageId" = c."PageId" AND r."ActiveTo" IS NULL
                WHERE c."PageId" = {GuidList.Any(":ids")} AND (c."Sequence" > coalesce(c."LastSavedSequence", 0) OR (:recentCounts AND c."UpdatedAt" > :recent))
                """).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("Recent", NHibernateUtil.Boolean)
            .AddScalar("Projection", NHibernateUtil.String).AddScalar("Working", NHibernateUtil.String)
            .SetParameter("ids", GuidList.Param(pages)).SetParameter("recentCounts", recentCounts)
            .SetParameter("recent", DateTime.UtcNow - PassDelay, NHibernateUtil.UtcDateTime).ListAsync<object[]>(ct))
            .Where(row => (bool)row[1] || !Unchanged(row[2] as string, row[3] as string)).Select(row => (Guid)row[0]).ToHashSet();

    /// <summary>The stored envelope in today's shape: the read repair and the current version of <c>atlas-requirements</c>; the same
    /// string when nothing changes.</summary>
    internal static string Convert(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return json;
        }
        if (root?["document"] is not JsonObject document)
        {
            return json;
        }
        var changed = LegacyDocumentRepair.Repair(document);
        var current = CollaborationSchema.CurrentPluginVersions[Plugin];
        foreach (var plugin in (root["plugins"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (plugin["id"] is JsonValue id && id.TryGetValue<string>(out var name) && name == Plugin
                && plugin["version"] is JsonValue version && version.TryGetValue<int>(out var number) && number < current)
            {
                plugin["version"] = current;
                changed = true;
            }
        }
        return changed ? root.ToJsonString(RichDocument.JsonWrite) : json;
    }

    /// <summary>
    /// Whether the live document reads as the working revision does, so its unsaved updates change nothing a reset could lose: the
    /// editor shows no change for them either (it compares the two as Markdown), so nobody would ever save them and the page would be
    /// deferred pass after pass (3.2.0, the contact split). False when no snapshot covers the newest update.
    /// </summary>
    private static bool Unchanged(string? projection, string? working)
    {
        if (projection is null || working is null)
        {
            return false;
        }
        try
        {
            var live = JsonNode.Parse(working, documentOptions: MarkdownDocumentValidator.JsonOptions)!.AsObject();
            live["document"] = JsonNode.Parse(projection, documentOptions: MarkdownDocumentValidator.JsonOptions);
            return Rules.ValidateDocument(live.ToJsonString()).Markdown == Rules.ValidateDocument(working).Markdown;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>A working or published revision of a page not purged (in the trash too, a template too) that declares the plugin in the
    /// 3.3 shape: version 1 (stored JSON is compact, the declaration's id comes first) or a summary block; or with an ordered list, which
    /// may be a history line read as nested lists (3.5.1, <see cref="LegacyDocumentRepair.HistoryDateList"/>): a revision the repair
    /// leaves as it is is read, not written. Converted pages without ordered lists are not read. On any page: a disclosure (3.6,
    /// <see cref="LegacyDocumentRepair.DisclosureHeading"/>), of any shape.</summary>
    private const string Holds = """
        p."PurgedAt" IS NULL AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId") AND (r."ContentJson" LIKE '%"atlas-requirements"%'
          AND (r."ContentJson" LIKE '%requirementSummary%' OR r."ContentJson" LIKE '%"atlas-requirements","version"_1}%' OR r."ContentJson" LIKE '%"orderedList"%')
          OR r."ContentJson" LIKE '%"type":"disclosure"%')
        """;

    /// <summary>The first <c>:pages</c> such pages after <c>:after</c> by id.</summary>
    private const string NextPages = $"""
        SELECT DISTINCT r."PageId" FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId" WHERE {Holds} AND r."PageId" > :after ORDER BY r."PageId" LIMIT :pages
        """;
}
