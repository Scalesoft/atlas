using Atlas.Server.Features.Jobs;
using Atlas.Server.Features.SearchGaps.Commands;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Retention;
using Wolverine;

namespace Atlas.Server.Features.SearchGaps;

/// <summary>
/// Writes what the in-memory recorder committed: every 10 s it sends the misses through Wolverine in a fresh scope (the
/// BackupScheduler pattern, no outbox), and at shutdown it sends the rest; while the log is full (OperationalRetention, which also
/// ages it out) it drops them. The search request never waits for any of this; a lost batch is a lost statistic, logged.
/// </summary>
public sealed class SearchMissFlusher(IServiceScopeFactory scopes, SearchMissRecorder recorder, AtlasDatabase database,
    TimeProvider clock, ILogger<SearchMissFlusher> logger, OptionalLogGate? gate = null, SystemServiceHealth? health = null) : BackgroundService
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private const int Batch = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!database.Initialized)
                {
                    continue;
                }
                try
                {
                    await FlushAsync(false, stoppingToken);
                    health?.Beat(SystemServices.SearchMisses);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Writing search misses failed; the drained batch is dropped and the next flush continues");
                    health?.Failed(SystemServices.SearchMisses, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Search miss flusher stopped");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!database.Initialized)
        {
            return;
        }
        try
        {
            await FlushAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Search misses pending at shutdown were dropped");
        }
    }

    /// <summary>Drains the recorder (every pending miss when <paramref name="all"/>) and records the misses in batches.</summary>
    public async Task FlushAsync(bool all, CancellationToken ct)
    {
        var misses = recorder.Drain(clock.GetUtcNow(), all);
        if (gate?.Accepts("SearchQueryLog") == false)
        {
            return;
        }
        foreach (var batch in misses.Chunk(Batch))
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new RecordSearchMisses(batch), ct);
        }
    }
}
