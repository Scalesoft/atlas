using Atlas.Server.Features.Jobs;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Auth.Recovery;

/// <summary>Publishes the server capabilities at startup and keeps retrying until the database answers.</summary>
internal sealed class ServerTokenInitializer : BackgroundService
{
    private readonly ServerAccessTokens m_tokens;
    private readonly ILogger<ServerTokenInitializer> m_logger;
    private readonly SystemServiceHealth m_health;

    public ServerTokenInitializer(ServerAccessTokens tokens, ILogger<ServerTokenInitializer> logger, SystemServiceHealth health)
    {
        m_health = health;
        m_tokens = tokens;
        m_logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await m_tokens.EnsureBootstrapAsync(stoppingToken);
                await m_tokens.EnsureRecoveryAsync(stoppingToken);
                m_health.Beat(SystemServices.ServerTokens);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m_logger.LogWarning("Server access tokens are not ready ({ErrorType}); initialization will retry.", ex.GetType().Name);
                m_health.Failed(SystemServices.ServerTokens, ex);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
