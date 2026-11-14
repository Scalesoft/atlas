using Atlas.Server.Features.AiIndex.Commands;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Platform.Jobs;
using Wolverine;

namespace Atlas.Server.Features.AiIndex;

/// <summary>
/// The index's upkeep (plan §10 "Údržba"): the consistency check of the live generations, which enqueues eligible pages
/// without a row, rows of an older revision and rows of pages that left the index (the worker settles them), and the purge
/// of Failed generations and of Retired ones older than 24 hours (O12), in small batches, and the full recompute of the
/// related-page lists (V4) of a generation whose build completed, after its activation and daily.
/// </summary>
public sealed class IndexMaintenanceRunner(IServiceScopeFactory scopes)
{
    public static readonly TimeSpan RetiredRetention = TimeSpan.FromHours(24);
    public const int PurgeBatch = 200;
    public const int NeighborBatch = 200;

    public async Task<(IndexConsistency Check, int PurgedGenerations)> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var check = await InvokeAsync<IndexConsistency>(new RunIndexMaintenance(now.UtcDateTime), ct);
        var generations = 0;
        while (true)
        {
            var purged = await InvokeAsync<PurgedIndexGenerations>(new PurgeIndexGenerations(now.UtcDateTime - RetiredRetention, PurgeBatch), ct);
            generations += purged.Generations;
            if (purged.Pages == 0)
            {
                break;
            }
        }
        await RefreshNeighborsAsync(now, ct);
        return (check, generations);
    }

    /// <summary>Recomputes the related-page lists of every live generation that is due, in batches of
    /// <see cref="NeighborBatch"/> pages (one short transaction each, under the generation's neighbour lock).</summary>
    /// <returns>The number of generations recomputed.</returns>
    public async Task<int> RefreshNeighborsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await InvokeAsync<NeighborRefreshesDue>(new FindNeighborRefreshes(now.UtcDateTime), ct);
        foreach (var generationId in due.GenerationIds)
        {
            Guid? after = null;
            do
            {
                after = (await InvokeAsync<RefreshedGenerationNeighbors>(
                    new RefreshGenerationNeighbors(generationId, after, NeighborBatch, now.UtcDateTime), ct)).Next;
            }
            while (after is not null);
        }
        return due.GenerationIds.Count;
    }

    private async Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, ct);
    }
}

/// <summary>The hourly job "ai-index.maintenance": <see cref="IndexMaintenanceRunner"/> at the clock's now (tests call the runner).</summary>
public sealed class IndexMaintenance(IndexMaintenanceRunner runner, TimeProvider clock) : IJobDefinition
{
    public string Id => "ai-index.maintenance";

    public string Title => "Údržba indexu AI";

    public string Description => "Zkontroluje konzistenci indexu, zařadí chybějící a zastaralé stránky, uklidí staré generace a přepočítá související stránky.";

    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromHours(1));

    public bool RunsWhenQuiet => true;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var (check, purged) = await runner.RunOnceAsync(clock.GetUtcNow(), ct);
        context.Summarize(new { check.Missing, check.Stale, check.Orphaned, purgedGenerations = purged });
        if (check.Missing + check.Stale + check.Orphaned + purged > 0)
        {
            context.Log.LogInformation("Index maintenance enqueued {Missing} missing, {Stale} stale and {Orphaned} orphaned pages and purged {Purged} generations",
                check.Missing, check.Stale, check.Orphaned, purged);
        }
    }
}
