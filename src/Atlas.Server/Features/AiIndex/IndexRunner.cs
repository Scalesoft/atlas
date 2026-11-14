using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.AiIndex.Commands;
using Atlas.Server.Features.AiIndex.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AiIndex;

[FluentMigrator.Migration(2026110873, "The AI index tasks are enqueued by code; their triggers and functions go")]
public sealed class IndexTasksToCodeMigration() : EmbeddedSchemaMigration("2026110873_index_tasks_to_code.sql");

/// <summary>
/// One round of the index worker (plan §10): claim in a short transaction, chunk and embed with no transaction open, write in
/// another short transaction that checks the claimed state is still current. Publication never waits for any of it.
/// </summary>
/// <remarks>
/// While the embeddings profile is unavailable (<c>AiAvailability</c>, A3's circuit breaker) a round does nothing
/// and spends no attempts, so the queue catches up within <see cref="NextIndexTaskDue.OutageRecheck"/> after a probe succeeds. An outage met during a round (timeout,
/// connection, 5xx, 429, auth …) ends the lease of the remaining tasks without an attempt and ends the round; any other
/// failure (an unusable answer, a bad request) backs the task off exponentially up to 15 minutes. Unchanged sections and
/// titles keep their vectors (by SHA-256 of the embedded text), so an edit embeds only what changed. Tests drive it with
/// <see cref="RunOnceAsync"/>; <see cref="IndexJob"/> calls it in production.
/// </remarks>
public sealed class IndexRunner(IServiceScopeFactory scopes, IAiClientFactory factory, ILogger<IndexRunner> logger)
{
    public const int ClaimBatch = 8;
    public const int EmbeddingBatch = 16;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Processes one claimed batch.</summary>
    /// <returns>The number of tasks finished (written, removed, discarded as stale or backed off); 0 when there was nothing to
    /// do or the round met an outage, so the caller waits for its next tick.</returns>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var work = await InvokeAsync<ClaimedIndexWork>(new ClaimIndexTasks(ClaimBatch, Lease, now.UtcDateTime), ct);
        var finished = work.Settled;
        for (var i = 0; i < work.Sources.Count; i++)
        {
            var source = work.Sources[i];
            try
            {
                var page = await EmbedAsync(source, ct);
                await InvokeAsync<StoredIndexPage>(page, ct);
                finished++;
            }
            catch (AiCallException exception) when (!CountsAsAttempt(exception.Code))
            {
                logger.LogInformation("Indexing paused: the embeddings profile answered {Outcome}; {Count} tasks wait without spending an attempt",
                    exception.Code, work.Sources.Count - i);
                foreach (var waiting in work.Sources.Skip(i))
                {
                    await InvokeAsync(new ReleaseIndexTask(waiting.GenerationId, waiting.PageId, waiting.Token, exception.Code, false, now.UtcDateTime, 1), ct);
                }
                return 0;
            }
            catch (Exception exception) when (exception is AiCallException or AiConfigurationException)
            {
                var code = exception is AiCallException call ? call.Code : AiOutcome.BadRequest;
                logger.LogWarning("Indexing page {PageId} of generation {GenerationId} failed: {Outcome}; it backs off", source.PageId, source.GenerationId, code);
                await InvokeAsync(new ReleaseIndexTask(source.GenerationId, source.PageId, source.Token, code, true, now.UtcDateTime, IndexBackoff.Jitter()), ct);
                finished++;
            }
        }
        return finished;
    }

    /// <summary>An outage of the profile spends no attempt; neither does a caller's cancellation or a busy gate.</summary>
    private static bool CountsAsAttempt(string code)
        => !(AiOutcome.IsAvailabilityFailure(code) || code is AiOutcome.Canceled or AiOutcome.Busy);

    private async Task<StoreIndexedPage> EmbedAsync(IndexSource source, CancellationToken ct)
    {
        var profile = source.Profile;
        var titleText = KnowledgeChunker.TitleText(source.Title, profile.DocumentPrefix);
        var titleHash = KnowledgeChunker.Hash(titleText);
        var titleKey = VectorText.Hex(titleHash);
        var chunks = source.IsTemplate ? [] : KnowledgeChunker.Chunk(source.Title, source.Markdown, source.Headings, profile.DocumentPrefix);
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.Equals(titleKey, source.ExistingTitleHash, StringComparison.Ordinal))
        {
            wanted.TryAdd(titleKey, titleText);
        }
        foreach (var chunk in chunks)
        {
            var key = VectorText.Hex(chunk.TextHash);
            if (!source.ExistingChunkHashes.Contains(key))
            {
                wanted.TryAdd(key, chunk.EmbeddingText);
            }
        }
        var vectors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wanted.Count > 0)
        {
            using var generator = factory.Embeddings(profile, AiTaskCatalog.IndexEmbed);
            foreach (var batch in wanted.Chunk(EmbeddingBatch))
            {
                var embeddings = await generator.GenerateAsync(batch.Select(x => x.Value), cancellationToken: ct);
                for (var i = 0; i < batch.Length; i++)
                {
                    vectors[batch[i].Key] = VectorText.Format(embeddings[i].Vector.Span);
                }
            }
        }
        return new StoreIndexedPage(source.GenerationId, source.PageId, source.Token, source.RevisionId, source.IsTemplate, titleHash,
            vectors.GetValueOrDefault(titleKey),
            chunks.Select(chunk => new IndexedChunk(chunk.Ordinal, chunk.HeadingId, chunk.HeadingTitle, chunk.Text, chunk.TextHash,
                vectors.GetValueOrDefault(VectorText.Hex(chunk.TextHash)))).ToArray());
    }

    private async Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, ct);
    }

    private async Task InvokeAsync(object message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message, ct);
    }
}

/// <summary>
/// The index queue's job (3.3 plan §1 row 18, only <c>AddAiIndexBackgroundServices</c>): rounds of <see cref="IndexRunner"/> while
/// a round finishes work, at the time the queue's own data says (<see cref="NextIndexTaskDue"/>), asked again when tasks are
/// enqueued (a publication, a generation's start or check, a space's inclusion), by an AI settings save and by the hourly maintenance.
/// The attempts, backoff and leases stay in the IndexTask rows; leases expire after a restart, so nothing is lost, and a building
/// generation is never activated by it.
/// </summary>
public sealed class IndexJob(IndexRunner runner, TimeProvider clock) : IJobDefinition
{
    public const string JobId = "ai-index.index";

    /// <summary>The soonest next round after one ended: a round that met an outage or a busy model ends with tasks still due.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

    public string Id => JobId;
    public string Title => "Indexace pro AI";
    public string Description => "Rozdělí publikované stránky na části a uloží jejich embeddingy do indexu, ze kterého odpovídá asistent a hledání podle významu.";
    public JobSchedule? Schedule => JobSchedule.FromData;

    public async Task<DateTime?> NextDueAsync(IServiceProvider services, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var scope = services.CreateScope();
        var due = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<DateTime?>(new NextIndexTaskDue(now), ct);
        return due < now + Pause ? now + Pause : due;
    }

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var total = 0;
        int finished;
        while ((finished = await runner.RunOnceAsync(clock.GetUtcNow(), ct)) > 0)
        {
            total += finished;
            context.Summarize(new { tasks = total });
            // A step per round: the summary is stored and the jobs feed tells the index flow (Správa wiki → AI), which follows the build.
            await context.StepAsync((_, _) => Task.FromResult(total), ct);
        }
        context.Log.LogInformation("Zpracováno {Count} úloh fronty indexu", total);
    }
}

/// <summary>A page's publication, trash, restore, move, template flag or silent rebuild enqueues its index tasks (<see cref="IndexTasks"/>;
/// the trigger TR_Page_IndexTask did before migration 2026110873): the index job asks its queue again.</summary>
public sealed record IndexTaskRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message is { ContentType: "atlas.page", RevisionKind: not "working" };
}

public static class IndexTaskRevisionHandler
{
    public static async Task Handle(IndexTaskRevision delivery, ISession session, IJobRequests requests, CancellationToken ct)
    {
        if (!IndexTaskRevision.Takes(delivery.Event))
        {
            return;
        }
        await IndexTasks.EnqueuePageAsync(session, delivery.Event.ResourceId, delivery.Event.RevisionKind == "rebuild", ct);
        await requests.RescheduleAsync(IndexJob.JobId, ct);
    }
}

/// <summary>The index tasks a change enqueues (one per live generation and page; a repeated change restarts the task's backoff).</summary>
public static class IndexTasks
{
    /// <summary>Nothing without a live generation, nothing for a page that no longer exists, nor for one that was never published and
    /// is not indexed (an unpublished page is not in the index).</summary>
    /// <param name="onlyIfInconsistent">The silent <c>rebuild</c> of the catalog (a restore, a migration) changes nothing the index reads: a
    /// page is enqueued only when the index disagrees with it (not indexed but eligible, indexed but not eligible, another revision or
    /// template flag), as the trigger did by comparing the page's changed columns.</param>
    public static Task EnqueuePageAsync(ISession session, Guid pageId, bool onlyIfInconsistent, CancellationToken ct) => session.CreateSQLQuery($"""
            INSERT INTO "IndexTask"("GenerationId","PageId","EnqueuedAt","NextAttemptAt")
            SELECT g."Id", p."Id", clock_timestamp(), now() FROM "IndexGeneration" g CROSS JOIN "Page" p
            LEFT JOIN "KnowledgePage" k ON k."GenerationId" = g."Id" AND k."PageId" = p."Id"
            WHERE g."State" IN ('Building','Active') AND p."Id" = :page
              AND (p."PublishedRevisionId" IS NOT NULL OR k."PageId" IS NOT NULL)
              AND (NOT :inconsistent OR CASE WHEN k."PageId" IS NULL THEN {IndexStore.EligibleRow("p")}
                ELSE NOT {IndexStore.EligibleRow("p")} OR k."RevisionId" <> p."PublishedRevisionId" OR k."IsTemplate" <> p."IsTemplate" END)
            ON CONFLICT ("GenerationId","PageId") DO UPDATE SET "EnqueuedAt" = EXCLUDED."EnqueuedAt",
              "NextAttemptAt" = EXCLUDED."NextAttemptAt", "Attempts" = 0, "LastErrorCode" = NULL
            """).SetParameter("page", pageId).SetParameter("inconsistent", onlyIfInconsistent).ExecuteUpdateAsync(ct);

    /// <summary>A changed inclusion of a space (G5): its published pages, so an excluded space leaves the index and an included one enters it.</summary>
    public static Task EnqueueSpaceAsync(ISession session, Guid spaceId, CancellationToken ct) => session.CreateSQLQuery("""
            INSERT INTO "IndexTask"("GenerationId","PageId","EnqueuedAt","NextAttemptAt")
            SELECT g."Id", p."Id", clock_timestamp(), now() FROM "IndexGeneration" g CROSS JOIN "Page" p
            WHERE g."State" IN ('Building','Active') AND p."SpaceId" = :space AND p."PublishedRevisionId" IS NOT NULL
            ON CONFLICT ("GenerationId","PageId") DO UPDATE SET "EnqueuedAt" = EXCLUDED."EnqueuedAt",
              "NextAttemptAt" = EXCLUDED."NextAttemptAt", "Attempts" = 0, "LastErrorCode" = NULL
            """).SetParameter("space", spaceId).ExecuteUpdateAsync(ct);
}
