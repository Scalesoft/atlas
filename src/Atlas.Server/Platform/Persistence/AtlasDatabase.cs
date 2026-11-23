using NHibernate;
using NHibernate.Cfg;
using Npgsql;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Persistence;

/// <summary>Owns the NHibernate session factory. Initialization runs migrations once; sessions are cheap afterwards.</summary>
public sealed class AtlasDatabase : IDisposable
{
    private const string NotConfiguredMessage = "Databáze není nastavená. Nastavte ConnectionStrings:atlas-db a restartujte aplikaci.";
    private const string NotReadyMessage = "Databáze není dostupná. Zkontrolujte připojení a serverové logy.";
    private readonly IConfiguration m_configuration;
    private readonly ILogger<AtlasDatabase> m_logger;
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private ISessionFactory? m_factory;
    // The requirement a refused migration named (e.g. pgvector), shown by every request until a retry succeeds.
    private volatile string? m_unsupportedServer;

    public AtlasDatabase(IConfiguration configuration, ILogger<AtlasDatabase> logger)
    {
        m_configuration = configuration;
        m_logger = logger;
    }

    public bool Configured => !string.IsNullOrWhiteSpace(m_configuration.GetConnectionString("atlas-db"));

    public bool Initialized => m_factory is not null;

    /// <summary>Runs migrations and builds the session factory. Idempotent; a no-op without a connection string.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (m_factory is not null || !Configured)
        {
            return;
        }
        await m_gate.WaitAsync(ct);
        try
        {
            if (m_factory is not null)
            {
                return;
            }
            var connectionString = m_configuration.GetConnectionString("atlas-db")!;
            var owner = OwnerConnectionString;
            try
            {
                await CollationVersion.RefreshAsync(owner, m_logger, ct);
                await AtlasMigrations.MigrateAsync(owner, ct);
                if (owner != connectionString)
                {
                    await RuntimeDatabaseRole.EnsureAsync(owner, connectionString, ct);
                }
                await DatabaseStatistics.RefreshAccessAsync(owner, ct);
                // A connection string that already prepares would keep the migrations' prepared plans in its pool.
                NpgsqlConnection.ClearPool(new NpgsqlConnection(owner));
            }
            catch (Exception ex) when (FindUnsupportedServer(ex) is { } unsupported)
            {
                // A server the schema cannot run on (e.g. 2026100520 without pgvector): the migration's own Czech message names
                // the requirement and the upgrade procedure, so the administrator gets it instead of a generic failure.
                m_logger.LogError("Schema initialization failed: the database server lacks a required feature. {Requirement}", unsupported);
                m_unsupportedServer = unsupported;
                throw new ApiException(503, unsupported);
            }
            catch (Exception ex) when (ex is not ApiException and not OperationCanceledException)
            {
                m_logger.LogWarning("Schema initialization failed ({ErrorType}).", ex.GetType().Name);
                throw new ApiException(503, "Inicializace databáze se nezdařila. Zkontrolujte konfiguraci a verzi databáze.");
            }
            var cfg = new Configuration();
            cfg.DataBaseIntegration(db =>
            {
                db.ConnectionString = WithAutoPrepare(connectionString);
                db.Dialect<NHibernate.Dialect.PostgreSQL83Dialect>();
                db.Driver<AtlasNpgsqlDriver>();
                db.LogSqlInConsole = false;
                db.LogFormattedSql = false;
                db.Timeout = 30;
            });
            cfg.AddMapping(AtlasMappings.Create().CompileMappingForAllExplicitlyAddedEntities());
            cfg.AppendListeners(NHibernate.Event.ListenerType.PreInsert, new NHibernate.Event.IPreInsertEventListener[] { ContentRowEffects.Instance });
            cfg.AppendListeners(NHibernate.Event.ListenerType.PostInsert, new NHibernate.Event.IPostInsertEventListener[] { ContentRowEffects.Instance });
            cfg.AppendListeners(NHibernate.Event.ListenerType.PostUpdate, new NHibernate.Event.IPostUpdateEventListener[] { ContentRowEffects.Instance });
            cfg.AppendListeners(NHibernate.Event.ListenerType.PostDelete, new NHibernate.Event.IPostDeleteEventListener[] { ContentRowEffects.Instance });
            cfg.AppendListeners(NHibernate.Event.ListenerType.Flush, new NHibernate.Event.IFlushEventListener[] { ContentRowEffects.Instance });
            cfg.AppendListeners(NHibernate.Event.ListenerType.Autoflush, new NHibernate.Event.IAutoFlushEventListener[] { ContentRowEffects.Instance });
            var integrity = new IntegrityRules();
            cfg.AppendListeners(NHibernate.Event.ListenerType.PreUpdate, new NHibernate.Event.IPreUpdateEventListener[] { integrity });
            cfg.AppendListeners(NHibernate.Event.ListenerType.PreDelete, new NHibernate.Event.IPreDeleteEventListener[] { integrity });
            m_factory = cfg.BuildSessionFactory();
            m_unsupportedServer = null;
        }
        catch (NpgsqlException ex)
        {
            m_logger.LogWarning("Database initialization failed ({ErrorType}).", ex.GetType().Name);
            throw new ApiException(503, NotReadyMessage);
        }
        finally
        {
            m_gate.Release();
        }
    }

    /// <summary>
    /// Sessions prepare the statements they repeat: planning cost far more than execution (a page bundle 21 vs 2.6 ms; over HTTP on the
    /// production copy the notifications' unread count 13.6 vs 3.7 ms, a page bundle 30 vs 16 ms). A failed command forgets its prepared
    /// statements (<see cref="PreparedStatements"/>, an Npgsql 10.0.3 bug). The configured string's own Auto Prepare settings win. The
    /// session pool gets its own string, so it is created after the migrations and never holds a plan from before them; Npgsql
    /// re-prepares once on a changed result type anyway. Needs a direct PostgreSQL connection: docker-compose.yml connects to wiki-db
    /// with no transaction-mode pgbouncer in between, which would drop prepared statements between transactions.
    /// </summary>
    public static string WithAutoPrepare(string connectionString)
    {
        if (connectionString.Replace(" ", "").Contains("AutoPrepare", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }
        return new NpgsqlConnectionStringBuilder(connectionString) { MaxAutoPrepare = 256, AutoPrepareMinUsages = 2 }.ConnectionString;
    }

    /// <summary>The message of a migration that refused the server (SQLSTATE 0A000 feature_not_supported), anywhere in the chain.</summary>
    private static string? FindUnsupportedServer(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            if (error is PostgresException { SqlState: PostgresErrorCodes.FeatureNotSupported } postgres)
            {
                return postgres.MessageText;
            }
        }
        return null;
    }

    /// <summary>Opens a session from the initialized factory. Callers own disposal (DI scope or using).</summary>
    public ISession OpenSession()
    {
        if (m_factory is { } factory)
        {
            return factory.OpenSession();
        }
        throw new ApiException(503, Configured ? m_unsupportedServer ?? NotReadyMessage : NotConfiguredMessage);
    }

    /// <summary>The schema owner's connection (<see cref="RuntimeDatabaseRole"/>): ConnectionStrings:atlas-db-owner, else atlas-db.</summary>
    private string OwnerConnectionString => m_configuration.GetConnectionString(RuntimeDatabaseRole.OwnerConnectionName) is { Length: > 0 } owner
        ? owner : m_configuration.GetConnectionString("atlas-db")!;

    /// <summary>A session as the schema's owner, for the installation restore, which empties the audit tables the runtime role may not
    /// touch. Without an owner connection it is <see cref="OpenSession"/> and the connection is null; otherwise the caller disposes the
    /// connection after the session.</summary>
    public async Task<(ISession Session, NpgsqlConnection? Connection)> OpenOwnerSessionAsync(CancellationToken ct)
    {
        var session = OpenSession();
        if (OwnerConnectionString == m_configuration.GetConnectionString("atlas-db"))
        {
            return (session, null);
        }
        session.Dispose();
        var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(ct);
        return (m_factory!.WithOptions().Connection(connection).OpenSession(), connection);
    }

    public void Dispose()
    {
        m_factory?.Dispose();
        m_gate.Dispose();
    }
}
