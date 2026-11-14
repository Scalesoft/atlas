using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using FluentMigrator;
using NHibernate.Criterion;
using Wolverine;

namespace Atlas.Server.Features.Collaboration;

[Migration(2026110997, "3.6: the server seeds the live state of every page; the job collaboration.live-seeds creates the missing ones")]
public sealed class LiveSeedsMigration() : EmbeddedSchemaMigration("2026110997_live_seeds.sql");

/// <summary>
/// 3.6 (one editor, J1.1): the server keeps a live state (Yjs) of every page's current revision, so the page bundle always carries it and
/// the page editor builds once from it, with no seed of its own and no stand-in editor beside it; the MCP's live writes find it too.
/// The state is the browser's seed of the stored rich JSON (<see cref="ILiveWriteAuthority.SeedAsync"/>, parity-tested in
/// YjsDocumentWriterTests), stored as a pushed initial snapshot is (FlushDocument). One winner: the seed is
/// built and stored under the hub's document gate (<see cref="LiveDocumentWriter.SeedAsync"/>), where a browser's seed is decided too,
/// on the generation <see cref="PrepareLiveSeed"/> fixed under the page row lock. Requested by every page announcement
/// (<see cref="LiveSeedRevision"/>), by an open whose bundle found none (PagesEndpoints) and by <see cref="LiveSeedsJob"/>.
/// Best effort: a page it cannot seed (content the editor refuses) is seeded by its first editor as before,
/// and the hub logs that seed as a warning.
/// </summary>
public sealed class LiveSeeds(IServiceScopeFactory scopes, LiveDocumentWriter writer, ILiveWriteAuthority authority, IHostApplicationLifetime lifetime,
    ILogger<LiveSeeds> log)
{
    /// <summary>How long an open waits for its page's seed: past it the bundle comes without the live state and the editor joins the page
    /// (POST collaboration), whose join waits for the seed under the document gate and hydrates from it.</summary>
    public static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(2);

    /// <summary><see cref="EnsureAsync"/> for an open, waited for no longer than <see cref="OpenWait"/>; the seed itself runs on to its end
    /// (only the host's stop cancels it), so a slow seed still lands and is never cut off half done.</summary>
    public async Task<bool> EnsureWithinAsync(Guid pageId, CancellationToken ct)
    {
        try
        {
            return await EnsureAsync(pageId, lifetime.ApplicationStopping).WaitAsync(OpenWait, ct);
        }
        catch (TimeoutException)
        {
            log.LogWarning("The server's seed of page {PageId} took longer than an open waits; the editor's join waits for it", pageId);
            return false;
        }
    }

    /// <summary>Revisions the server could not seed, by page: not tried again (no page lock, no build) until the page has another
    /// revision. Per process, as RequirementRepairMemory: another instance or a restart tries once more.</summary>
    private readonly ConcurrentDictionary<Guid, Guid> m_refused = new();

    /// <returns>True when the page's live state is current now (it was, or this call or a concurrent one stored it).</returns>
    public async Task<bool> EnsureAsync(Guid pageId, CancellationToken ct)
    {
        LiveSeedTarget? target = null;
        try
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                target = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<LiveSeedTarget>(
                    new PrepareLiveSeed(pageId, m_refused.TryGetValue(pageId, out var refused) ? refused : null), ct);
            }
            if (target.ContentJson is null)
            {
                return target.Current;
            }
            using var content = JsonDocument.Parse(target.ContentJson, MarkdownDocumentValidator.JsonOptions);
            var document = content.RootElement.GetProperty("document");
            var schema = CollaborationSchema.Deserialize(target.SchemaJson)!;
            var started = Stopwatch.GetTimestamp();
            if (await writer.SeedAsync(new WikiDocumentId(pageId, target.Generation).ToString(), schema, token => authority.SeedAsync(schema, document, token), ct))
            {
                log.LogInformation("Live state of page {PageId} seeded by the server in {Elapsed} ms ({Bytes} bytes of content)", pageId,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, target.ContentJson.Length);
            }
            m_refused.TryRemove(pageId, out _);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Content the authority refuses stays refused; a busy authority's deadline is tried again.
            if (target?.ContentJson is not null && !error.Message.StartsWith("[AUTHORITY_TIMEOUT]", StringComparison.Ordinal))
            {
                m_refused[pageId] = target.RevisionId;
            }
            log.LogWarning(error, "The server could not seed the live state of page {PageId}; its first editor seeds it", pageId);
            return false;
        }
    }
}

/// <summary>The live seed's own delivery of every page announcement: it seeds from the current state, so a repeat changes nothing.</summary>
public sealed record LiveSeedRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message.ContentType == PagesAtlasModule.ContentType;
}

public static class LiveSeedRevisionHandler
{
    public static Task Handle(LiveSeedRevision delivery, LiveSeeds seeds, CancellationToken ct)
        => LiveSeedRevision.Takes(delivery.Event) ? seeds.EnsureAsync(delivery.Event.ResourceId, ct) : Task.CompletedTask;
}

/// <summary>Where the live seeds stand: pages looked at up to <paramref name="After"/> by id, <paramref name="Current"/> of them with a current state.</summary>
public sealed record LiveSeedProgress(Guid After, int Pages, int Current);

/// <summary>
/// The live state of every page that lacks a current one, in short checkpointed batches (migration 2026110997 queues it, as does a backup
/// restore; an administrator may run it again from Úlohy): idempotent, a page with a current state costs two reads.
/// </summary>
public sealed class LiveSeedsJob : IJobDefinition
{
    public const string JobId = "collaboration.live-seeds";
    public const int Batch = 50;

    public string Id => JobId;

    public string Title => "Živý stav stránek";

    public string Description => "Připraví na serveru živý stav každé stránky, které chybí, aby se editor otevřel rovnou z něj.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var seeds = context.Services.GetRequiredService<LiveSeeds>();
        var progress = context.Progress<LiveSeedProgress>() ?? new LiveSeedProgress(Guid.Empty, 0, 0);
        while (true)
        {
            var before = progress;
            IList<Guid> pages = [];
            // The read is a step of its own that keeps the checkpoint; the seeds run outside every transaction (LiveDocumentWriter.SeedAsync).
            await context.StepAsync(async (session, token) =>
            {
                pages = await session.QueryOver<Page>().Where(Restrictions.Gt(Projections.Property<Page>(x => x.Id), before.After)).And(x => !x.IsTrashed)
                    .OrderBy(x => x.Id).Asc.Select(x => x.Id).Take(Batch).ListAsync<Guid>(token);
                return before;
            }, ct);
            if (pages.Count == 0)
            {
                break;
            }
            var current = 0;
            foreach (var page in pages)
            {
                current += await seeds.EnsureAsync(page, ct) ? 1 : 0;
            }
            progress = await context.StepAsync((_, _) => Task.FromResult(new LiveSeedProgress(pages[^1], before.Pages + pages.Count, before.Current + current)), ct);
            context.Summarize(new { pages = progress.Pages, current = progress.Current });
        }
        context.Log.LogInformation("Živý stav: {Current} z {Pages} stránek je aktuální", progress.Current, progress.Pages);
    }
}
