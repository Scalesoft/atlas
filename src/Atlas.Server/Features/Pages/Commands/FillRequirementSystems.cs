using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Validation;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Commands;

// Maintenance a migration requests and an administrator may run again from Úlohy (atlas_run_job), not a user's command: no MCP tool of its own.

/// <summary>One batch of <see cref="RequirementSystemsJob"/> over the checkpoint of the run <paramref name="RunId"/>.</summary>
public sealed record FillRequirementSystems(long RunId);

/// <summary>The checkpoint of a run (JobRun.Progress) and its numbers: pages read up to <see cref="AfterPageId"/>, revisions rewritten,
/// requirements that took a service, pages deferred for their live edits (<see cref="Waiting"/> names them), revisions today's rules
/// refuse and pages whose batch failed. <see cref="Only"/>: a follow-up run reads only these pages, the ones the run before it deferred,
/// so a requested run offers each requirement a service once.</summary>
public sealed record RequirementSystemsProgress(Guid AfterPageId, int Pages, int Revisions, int Filled, int Deferred, int Refused, int Failed = 0,
    IReadOnlyList<Guid>? Only = null, IReadOnlyList<Guid>? Waiting = null)
{
    public static readonly RequirementSystemsProgress Empty = new(Guid.Empty, 0, 0, 0, 0, 0);
}

/// <summary>What a batch read (0: the pass is done) and the run's numbers after it.</summary>
public sealed record RequirementSystemsRound(int Pages, RequirementSystemsProgress Progress);

/// <summary>
/// A batch of the 4.0 fill of requirement services (owner, 2026-11-11: a deployment board draws the external services, and pages made
/// before the catalog named them have none): the next page whose working or published revision holds a requirement heading with a
/// provenance (<c>from</c> = <c>catalogPageId#headingId</c>, written by the insertion from the catalog and by the 3.6 conversion), under
/// the conversion's key and its page lock (the order of pages.requirements-headings). A requirement whose own <c>system</c> is empty
/// takes the <c>system</c> of the catalog heading it came from, read from the published revision of a page marked as a catalog and not
/// in the trash; one that has a service keeps it, one without a provenance or whose catalog heading names none stays empty (nothing is
/// guessed from names). Rewritten in place by the conversions' <see cref="RequirementPageConversion.RewriteAsync"/> (the revision keeps its author and time, the requirement index is refreshed by the next
/// read), validated and with its Markdown written again; history stays as stored. No row goes to <c>RequirementConversionBackup</c>:
/// that table holds the originals of the 3.6 conversion and tells which pages it converted (RequirementPageConversion.KeyMapAsync); the
/// fill only adds services the catalog still names, and the run's log names each page and how many it took.
/// <para>One page a batch, one transaction: a page whose batch throws is skipped and counted (<see cref="SkipAsync"/>). Nothing is
/// filled while pages.requirements-headings still has a run waiting or under way (it writes the provenance): the run then asks for
/// another one later. Unsaved live edits nobody is typing are stored first (<see cref="UnsavedAsync"/>, as the heading conversion
/// does); a page with live activity in the last <see cref="ConvertRequirementsOverviewHandler.PassDelay"/>, or with unsaved edits no
/// author may save, is deferred. Only pages with such recent activity bring a follow-up run, and that run reads the deferred pages
/// alone: a service a person cleared after the page was filled is not filled again by the follow-ups, and a page nobody will ever
/// save does not keep the job running.</para>
/// </summary>
public sealed class FillRequirementSystemsHandler(ISession session, IAtlasLocks locks, IJobRequests jobs, Outbox outbox, ILogger<FillRequirementSystemsHandler> logger)
{
    public async Task<RequirementSystemsRound> Handle(FillRequirementSystems command, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        var progress = await ProgressAsync(session, command.RunId, ct);
        if (progress == RequirementSystemsProgress.Empty && await session.CreateSQLQuery(Converting).AddScalar("Waiting", NHibernateUtil.Boolean).UniqueResultAsync<bool>(ct))
        {
            await jobs.RequestAsync(RequirementSystemsJob.JobId, "Čeká na převod požadavků na nadpisy", notBefore: DateTime.UtcNow + ConvertRequirementsOverviewHandler.PassDelay, ct: ct);
            return new RequirementSystemsRound(0, progress);
        }
        if (await NextAsync(session, progress, ct) is not { } pageId)
        {
            if (progress.Waiting is { Count: > 0 } waiting && await ActiveAsync(waiting, ct)
                && await jobs.RequestAsync(RequirementSystemsJob.JobId, "Stránky, na kterých se právě píše", notBefore: DateTime.UtcNow + ConvertRequirementsOverviewHandler.PassDelay, ct: ct) is { } followUp)
            {
                await StoreAsync(session, followUp, RequirementSystemsProgress.Empty with { Only = waiting }, ct);
            }
            return new RequirementSystemsRound(0, progress);
        }
        // The conversion's key first, then the page: the order of the heading conversion's batch.
        await locks.AcquireAdvisoryAsync(RequirementPageConversion.LockKey, ct);
        var pages = await locks.LockPagesForUpdateAsync([pageId], ct);
        var systems = await CatalogSystemsAsync(ct);
        // Read under the lock: what a save committed meanwhile is what is filled.
        var revisions = await session.CreateSQLQuery($"""
                SELECT r."Id", r."PageId", r."ContentJson" FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
                WHERE r."PageId" = {GuidList.Any(":ids")} AND {Holds} ORDER BY r."PageId", r."Id"
                """).AddScalar("Id", NHibernateUtil.Guid).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("ContentJson", NHibernateUtil.String)
            .SetParameter("ids", GuidList.Param(pages)).ListAsync<object[]>(ct);
        var due = new List<(Guid Id, string Json, string Markdown, int Filled)>();
        var refused = 0;
        foreach (var row in revisions)
        {
            var (id, stored) = ((Guid)row[0], (string)row[2]);
            try
            {
                var (json, count) = Fill(stored, systems);
                if (count > 0)
                {
                    due.Add((id, json, Rules.ValidateDocument(json).Markdown, count));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A revision today's rules refuse stays as it is.
                logger.LogWarning("Revision {RevisionId} of page {PageId} keeps its requirements without services: {Message}", id, pageId, error.Message);
                refused++;
            }
        }
        var deferred = due.Count > 0 && (await ConvertRequirementsOverviewHandler.EditingAsync(session, [pageId], ct)).Count > 0;
        var (written, requirements) = (0, 0);
        foreach (var (id, json, markdown, count) in due.Where(_ => !deferred))
        {
            await RequirementPageConversion.RewriteAsync(session, id, pageId, json, markdown, ct, backup: false);
            written++;
            requirements += count;
        }
        if (written > 0)
        {
            // As the heading conversion: an in-place update announces nothing by itself.
            await Search.SearchDocuments.RefreshAsync(session, [pageId], ct);
            outbox.ContentRebuilt(pageId, PagesAtlasModule.ContentType);
            await new CollaborationCheckpoints(session).ResetAllAsync([pageId], ct);
            logger.LogInformation("Page {PageId}: {Requirements} requirements took their catalog's service in {Revisions} revisions.", pageId, requirements, written);
        }
        progress = progress with
        {
            AfterPageId = pageId, Pages = progress.Pages + 1, Revisions = progress.Revisions + written, Filled = progress.Filled + requirements,
            Deferred = progress.Deferred + (deferred ? 1 : 0), Refused = progress.Refused + refused, Waiting = deferred ? [.. progress.Waiting ?? [], pageId] : progress.Waiting,
        };
        await StoreAsync(session, command.RunId, progress, ct);
        return new RequirementSystemsRound(1, progress);
    }

    public static async Task<RequirementSystemsProgress> ProgressAsync(ISession session, long runId, CancellationToken ct)
        => await session.CreateSQLQuery("""SELECT CAST("Progress" AS text) AS "Progress" FROM "JobRun" WHERE "Id" = :id""")
            .AddScalar("Progress", NHibernateUtil.String).SetParameter("id", runId).UniqueResultAsync<string?>(ct) is { } json
            ? JsonSerializer.Deserialize<RequirementSystemsProgress>(json, JsonSerializerOptions.Web)!
            : RequirementSystemsProgress.Empty;

    private static Task StoreAsync(ISession session, long runId, RequirementSystemsProgress progress, CancellationToken ct)
        => session.CreateSQLQuery("""UPDATE "JobRun" SET "Progress" = CAST(:progress AS jsonb) WHERE "Id" = :id""")
            .SetParameter("progress", JsonSerializer.Serialize(progress, JsonSerializerOptions.Web)).SetParameter("id", runId).ExecuteUpdateAsync(ct);

    /// <summary>The next page of the run after its checkpoint: of the pages a follow-up is for, else of every page holding a provenance.</summary>
    private static async Task<Guid?> NextAsync(ISession session, RequirementSystemsProgress progress, CancellationToken ct)
        => progress.Only is { } only
            ? only.Where(id => id.CompareTo(progress.AfterPageId) > 0).OrderBy(id => id).Cast<Guid?>().FirstOrDefault()
            : (await session.CreateSQLQuery(NextPage).AddScalar("PageId", NHibernateUtil.Guid).SetParameter("after", progress.AfterPageId).ListAsync<Guid>(ct)).Cast<Guid?>().FirstOrDefault();

    /// <summary>The checkpoint past the next page, whose batch threw in its own transaction (rolled back): counted as failed, left as
    /// stored, tried again by the next requested run. Only while the checkpoint is still <paramref name="before"/>.</summary>
    public static async Task<RequirementSystemsProgress> SkipAsync(ISession session, long runId, Guid before, CancellationToken ct)
    {
        var progress = await ProgressAsync(session, runId, ct);
        if (progress.AfterPageId != before || await NextAsync(session, progress, ct) is not { } skipped)
        {
            return progress;
        }
        progress = progress with { AfterPageId = skipped, Pages = progress.Pages + 1, Failed = progress.Failed + 1 };
        await StoreAsync(session, runId, progress, ct);
        return progress;
    }

    /// <summary>The live document of the run's next page when it holds unsaved edits and had no activity in the last
    /// <see cref="ConvertRequirementsOverviewHandler.PassDelay"/>: <see cref="RequirementSystemsJob"/> stores it before the batch.</summary>
    public static async Task<CheckpointLivePage?> UnsavedAsync(ISession session, long runId, CancellationToken ct)
        => await NextAsync(session, await ProgressAsync(session, runId, ct), ct) is not { } pageId ? null
            : (await session.CreateSQLQuery("""
                    SELECT c."PageId", c."Generation" FROM "PageCollaboration" c
                    WHERE c."PageId" = :page AND c."Sequence" > coalesce(c."LastSavedSequence", 0) AND c."UpdatedAt" <= :recent
                    """).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("Generation", NHibernateUtil.Guid).SetParameter("page", pageId)
                .SetParameter("recent", DateTime.UtcNow - ConvertRequirementsOverviewHandler.PassDelay, NHibernateUtil.UtcDateTime).ListAsync<object[]>(ct))
                .Select(row => new CheckpointLivePage((Guid)row[0], (Guid)row[1])).FirstOrDefault();

    /// <summary>Whether a deferred page had live activity in the last <see cref="ConvertRequirementsOverviewHandler.PassDelay"/>: its
    /// editors will have gone by a follow-up. Unsaved edits of nobody in particular do not change by waiting.</summary>
    private async Task<bool> ActiveAsync(IReadOnlyList<Guid> pages, CancellationToken ct)
        => await session.CreateSQLQuery($"""
                SELECT EXISTS (SELECT 1 FROM "PageCollaboration" c WHERE c."PageId" = {GuidList.Any(":ids")} AND c."UpdatedAt" > :recent) AS "Active"
                """).AddScalar("Active", NHibernateUtil.Boolean).SetParameter("ids", GuidList.Param(pages))
            .SetParameter("recent", DateTime.UtcNow - ConvertRequirementsOverviewHandler.PassDelay, NHibernateUtil.UtcDateTime).UniqueResultAsync<bool>(ct);

    /// <summary>The service of every requirement heading that names one in the published revision of a catalog page not in the trash,
    /// by <c>pageId#headingId</c>. Catalogs are a few pages; read once a batch.</summary>
    private async Task<Dictionary<string, string>> CatalogSystemsAsync(CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
                SELECT p."Id", r."ContentJson" FROM "ProjectorCatalogPage" c JOIN "Page" p ON p."Id" = c."PageId" JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId"
                WHERE p."PurgedAt" IS NULL AND NOT p."IsTrashed" AND strpos(r."ContentJson", '"system":"') > 0
                """).AddScalar("Id", NHibernateUtil.Guid).AddScalar("ContentJson", NHibernateUtil.String).ListAsync<object[]>(ct);
        var systems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var heading in Requirements(Document((string)row[1])))
            {
                if (Text(heading, "system") is { Length: > 0 } system && Text(heading, "headingId") is { Length: > 0 } headingId)
                {
                    systems[RequirementHeadings.Reference((Guid)row[0], headingId)] = system;
                }
            }
        }
        return systems;
    }

    /// <summary>The stored envelope with the service of each requirement heading that has none and came from a catalog heading that names
    /// one, and how many took one; the same string and 0 when none did.</summary>
    internal static (string Json, int Filled) Fill(string json, IReadOnlyDictionary<string, string> systems)
    {
        var root = Parse(json);
        var filled = 0;
        foreach (var heading in Requirements(root?["document"]))
        {
            if (Text(heading, "system").Length == 0 && systems.TryGetValue(Text(heading, "from"), out var system))
            {
                heading["attrs"]!["system"] = system;
                filled++;
            }
        }
        return filled > 0 ? (root!.ToJsonString(RichDocument.JsonWrite), filled) : (json, 0);
    }

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Document(string json) => Parse(json)?["document"];

    /// <summary>Every requirement heading (a heading with a state) of the document, wherever it stands.</summary>
    private static IEnumerable<JsonObject> Requirements(JsonNode? node)
    {
        if (node is not JsonObject value)
        {
            yield break;
        }
        if (RichDocument.TypeOf(value) == "heading" && Text(value, "state").Length > 0)
        {
            yield return value;
        }
        foreach (var child in (value["content"] as JsonArray ?? []).SelectMany(Requirements))
        {
            yield return child;
        }
    }

    private static string Text(JsonObject node, string name) => node["attrs"]?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>A working or published revision of a page not purged that holds a provenance (stored JSON is compact).</summary>
    private const string Holds = $"""{RequirementPageConversion.Current} AND r."ContentJson" LIKE '%"from":"________-____-____-____-____________#%'""";

    /// <summary>The first such page after <c>:after</c> by id.</summary>
    private const string NextPage = $"""
        SELECT DISTINCT r."PageId" FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId" WHERE {Holds} AND r."PageId" > :after ORDER BY r."PageId" LIMIT 1
        """;

    /// <summary>Whether the conversion that writes the provenance still has a run waiting or under way.</summary>
    private const string Converting = $"""
        SELECT EXISTS (SELECT 1 FROM "JobRun" WHERE "JobId" = '{RequirementsHeadingsJob.JobId}' AND "State" IN ('queued', 'running')) AS "Waiting"
        """;
}
