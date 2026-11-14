using Atlas.Server.Features.Jobs;
using Atlas.Server.Platform.Jobs;
using Npgsql;

namespace Atlas.Server.Platform.Persistence;

/// <summary>
/// Runs migrations at startup and keeps retrying until the database answers. Scoped sessions never
/// initialize lazily, so without this loop a host that started before the database stays unusable.
/// </summary>
public sealed class AtlasDatabaseInitializer : BackgroundService
{
    private readonly AtlasDatabase m_database;
    private readonly AtlasDatabaseInitializerOptions m_options;
    private readonly ILogger<AtlasDatabaseInitializer> m_logger;
    private readonly SystemServiceHealth m_health;

    public AtlasDatabaseInitializer(AtlasDatabase database, AtlasDatabaseInitializerOptions options, ILogger<AtlasDatabaseInitializer> logger,
        SystemServiceHealth health)
    {
        m_health = health;
        m_database = database;
        m_options = options;
        m_logger = logger;
    }

    /// <summary>The first attempt finishes before the host reports started, exactly as it did before the retry loop.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await TryInitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No connection string is a decision, not an outage: the setup wizard reports it and nothing retries.
        while (m_database.Configured && !m_database.Initialized)
        {
            try
            {
                await Task.Delay(m_options.RetryInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await TryInitializeAsync(stoppingToken);
        }
        if (m_database.Initialized)
        {
            m_logger.LogInformation("Database is ready.");
        }
    }

    private async Task TryInitializeAsync(CancellationToken ct)
    {
        try
        {
            await m_database.InitializeAsync(ct);
            m_health.Beat(SystemServices.DatabaseInitializer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown during a connection attempt is not a failure worth logging.
        }
        catch (Exception ex) when (ex is ApiException or NpgsqlException)
        {
            m_logger.LogWarning("Database is not ready ({ErrorType}); retrying in {Seconds} s.", ex.GetType().Name, m_options.RetryInterval.TotalSeconds);
            m_health.Failed(SystemServices.DatabaseInitializer, ex);
        }
    }
}
