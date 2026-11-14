using Atlas.Server.Features.Jobs;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Platform.Live;

/// <summary>Revalidates every live subscription each <see cref="LiveRelay.RefreshInterval"/>, and ends the object subscriptions
/// that outlived their views (<see cref="ViewSessions.Linger"/>); access changes with a commit do not wait for it (AccessChanges
/// awaits <see cref="LiveRelay.AccessChangedAsync"/>).</summary>
public sealed class LiveRefreshService : BackgroundService
{
    private readonly LiveRelay m_relay;
    private readonly ViewSessions m_views;
    private readonly ILogger<LiveRefreshService> m_log;
    private readonly SystemServiceHealth m_health;

    public LiveRefreshService(LiveRelay relay, ViewSessions views, ILogger<LiveRefreshService> log, SystemServiceHealth health)
    {
        m_health = health;
        m_relay = relay;
        m_views = views;
        m_log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(LiveRelay.RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            m_views.ExpireLingering();
            try
            {
                await m_relay.RefreshAsync(null, stoppingToken);
                m_health.Beat(SystemServices.LiveRefresh);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                m_log.LogWarning(ex, "Periodic revalidation of live subscriptions failed; the next one retries");
                m_health.Failed(SystemServices.LiveRefresh, ex);
            }
        }
    }
}
