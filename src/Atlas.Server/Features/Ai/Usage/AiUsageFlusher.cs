using Atlas.Server.Features.Ai.Usage.Commands;
using Atlas.Server.Features.Jobs;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Retention;
using Wolverine;

namespace Atlas.Server.Features.Ai.Usage;

/// <summary>
/// Writes what <see cref="AiUsageRecorder"/> buffered: every 10 s one INSERT through Wolverine in a fresh scope (the
/// SearchMissFlusher pattern, no outbox), and at shutdown the rest; while the log is full (OperationalRetention, which also ages it
/// out after 180 days, O12) it drops them. No model call waits for any of this; a lost batch is a lost statistic, logged.
/// </summary>
public sealed class AiUsageFlusher(IServiceScopeFactory scopes, AiUsageRecorder recorder, AtlasDatabase database, TimeProvider clock,
    ILogger<AiUsageFlusher> logger, OptionalLogGate? gate = null, SystemServiceHealth? health = null) : BackgroundService
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

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
                    await FlushAsync(stoppingToken);
                    health?.Beat(SystemServices.AiUsage);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Writing AI usage failed; the drained batch is dropped and the next flush continues");
                    health?.Failed(SystemServices.AiUsage, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("AI usage flusher stopped");
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
            await FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI usage pending at shutdown was dropped");
        }
    }

    /// <summary>Drains the recorder and writes everything in one message (the handler splits only very large batches).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var entries = recorder.Drain();
        if (entries.Count == 0 || gate?.Accepts("AiUsage") == false)
        {
            return;
        }
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new RecordAiUsage(entries), ct);
    }
}
