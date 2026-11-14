using Atlas.Server.Features.Jobs;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Ai.Availability;

/// <summary>
/// Runs <see cref="AiProbeRunner"/> right after start (O13: AI is unverified until then), every <see cref="Tick"/> and at once
/// after a committed configuration change (a new revision is probed without waiting). Registered only by
/// <c>AddAiBackgroundServices</c>; tests call the runner. It never stops on an error and logs no upstream text.
/// </summary>
public sealed class AiProbeService(AiProbeRunner runner, AiConfigurationCache cache, AtlasDatabase database, TimeProvider clock,
    ILogger<AiProbeService> logger, SystemServiceHealth health) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (database.Initialized)
            {
                try
                {
                    await runner.RunOnceAsync(clock.GetUtcNow(), stoppingToken);
                    health.Beat(SystemServices.AiProbe);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Checking AI availability failed ({ErrorType}); the next tick retries", exception.GetType().Name);
                    health.Failed(SystemServices.AiProbe, exception);
                }
            }
            try
            {
                await cache.WaitForChangeAsync(Tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
        logger.LogDebug("AI availability probes stopped");
    }
}
