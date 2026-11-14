using FluentMigrator;
using JasperFx;
using Npgsql;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Runtime.Handlers;

namespace Atlas.Server.Platform.Jobs;

/// <summary>Runs one attempt of a run on the durable local queue <see cref="JobQueue.Name"/>; it runs outside the transaction middleware
/// and makes its own short transactions (§4.4).</summary>
public sealed record RunJob(long RunId, string JobId);

/// <summary>A planned occurrence of a scheduled job (Every, Cron, FromData). <paramref name="Revision"/> is the ticks of the Job.NextDueAt it
/// was planned for: an occurrence planned before the plan changed finds another value and does nothing.</summary>
public sealed record RequestJob(string JobId, long Revision);

/// <summary>A FromData job asks its data for the next due time, after the transaction of <see cref="IJobRequests.RescheduleAsync"/>
/// committed what its caller wrote.</summary>
public sealed record PlanJob(string JobId);

public sealed class JobQueueHandler
{
    /// <summary>A failed begin, plan or occurrence (a database blip) is tried again, later and later, never dropped: a dropped message
    /// would leave its run queued or end its plan's chain until the next start. Each one is safe to repeat.</summary>
    public static void Configure(HandlerChain chain) => chain.OnAnyException()
        .ScheduleRetryIndefinitely(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

    // Not the handler's token: Wolverine cancels it after its execution timeout (60 s), the run may take minutes.
    public static Task Handle(RunJob message, JobRunner runner) => runner.HandleAsync(message);

    public static Task Handle(RequestJob message, JobRequests requests, CancellationToken ct) => requests.OnScheduleAsync(message, ct);

    public static Task Handle(PlanJob message, JobRequests requests, CancellationToken ct) => requests.OnPlanAsync(message, ct);
}

/// <summary>Wolverine's durable messaging in Atlas's database (3.3 plan §4.4): the envelopes in the schema of the Atlas tables
/// (<see cref="WolverineStorageMigration"/>), the local queue of the jobs and the start of the jobs (<see cref="JobScheduler"/>). The server
/// uses it whenever it has a database, and so do the test hosts that run the queue; the other hosts route no job message, their
/// requests stay queued and a test runs them by <see cref="IJobRunner"/>.</summary>
public static class JobQueue
{
    public const string Name = "jobs";

    /// <summary>The short plan and request messages, so they never wait behind four long runs.</summary>
    public const string Plans = "job-plans";

    /// <summary>Whether the server runs durably: <c>Jobs:Durable</c>, by default whenever <c>ConnectionStrings:atlas-db</c> is set
    /// (the setup wizard runs without a database).</summary>
    public static bool IsDurable(IConfiguration configuration)
        => configuration.GetValue("Jobs:Durable", !string.IsNullOrWhiteSpace(configuration.GetConnectionString("atlas-db")));

    /// <param name="connectionString">Null for <c>codegen write</c>, which builds the configuration and never connects.</param>
    public static WolverineOptions UseAtlasDurability(this WolverineOptions options, string? connectionString)
    {
        var schema = new NpgsqlConnectionStringBuilder(connectionString).SearchPath?.Split(',')[0].Trim();
        // A container singleton, so the host disposes its pool.
        options.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString!));
        options.PersistMessagesWithPostgresqlUsingDataSourceFromContainer(provider => provider.GetRequiredService<NpgsqlDataSource>(),
            string.IsNullOrEmpty(schema) ? "public" : schema);
        options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        // One instance (decision O3): no leader election and no agent assignment.
        options.Durability.Mode = DurabilityMode.Solo;
        // At most 4 runs at once, so backups, indexing and PDF exports do not starve the connection pool. One run of a job at a
        // time is held by the database (JobRunner), not by partitioning: its slots would queue unrelated jobs behind a long one.
        options.LocalQueue(Name).UseDurableInbox().MaximumParallelMessages(4);
        options.PublishMessage<RunJob>().ToLocalQueue(Name);
        options.LocalQueue(Plans).UseDurableInbox();
        options.PublishMessage<RequestJob>().ToLocalQueue(Plans);
        options.PublishMessage<PlanJob>().ToLocalQueue(Plans);
        options.Services.AddHostedService<JobScheduler>();
        return options;
    }
}

/// <summary>Between AtlasDatabaseInitializer and Wolverine in the server (3.3 plan §4.4): Solo durability needs the migrated database at
/// its start, so without it the server says why and exits, and the restart policy starts it again.</summary>
public sealed class DurableStartGate(AtlasDatabase database, ILogger<DurableStartGate> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!database.Initialized)
        {
            logger.LogCritical("The database is unreachable or not migrated (see above), and the durable job queue cannot start without it: "
                + "the server exits and the restart policy starts it again.");
            throw new InvalidOperationException("The database is not ready for the durable job queue.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Once the hosted services started and the database is migrated: the Job rows, the startup and one-time runs, the plans of
/// the scheduled jobs and the runs requested by SQL or left by the last process (§4.2).</summary>
public sealed class JobScheduler(AtlasDatabase database, IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<JobScheduler> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // After every hosted service started, Wolverine among them.
            var started = new TaskCompletionSource();
            using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
            {
                await started.Task.WaitAsync(stoppingToken);
            }
            while (!database.Initialized)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            // Every step merges or repeats safely, so a failed start (a database blip, a plan's data) is tried again until it succeeds.
            while (true)
            {
                try
                {
                    await JobRunner.InTransactionAsync(scopes, async (services, _) =>
                    {
                        await services.GetRequiredService<JobRequests>().StartAsync(stoppingToken);
                        return true;
                    }, stoppingToken);
                    return;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Starting the background jobs failed; it is tried again in 30 seconds.");
                }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

[Migration(2026110401, "Wolverine durable message storage (3.3)")]
public sealed class WolverineStorageMigration() : EmbeddedSchemaMigration("2026110401_wolverine.sql");

[Migration(2026111300, "Wolverine 6.64: the index of scheduled envelopes")]
public sealed class WolverineScheduledIndexMigration() : EmbeddedSchemaMigration("2026111300_wolverine_scheduled_index.sql");
