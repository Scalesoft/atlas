using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Hub;

/// <summary>
/// Verifies the Hub identities against the Hub directory (3.3 plan §1 row 9): passes over the due identities, four at a time, at the
/// time the identities' own data says (<see cref="HubSynchronizer.NextDueAsync"/>) and when the connection is saved. The reservations,
/// locks, FailureCount and backoff stay in HubIdentityState; a login verifies its identity at once without the job.
/// </summary>
public sealed class HubIdentityJob(HubSynchronizer synchronizer) : IJobDefinition
{
    public const string JobId = "hub.identities";

    /// <summary>The soonest next pass after one: a verified identity is due again 60 s later, so identities spread over the minute by
    /// their logins are verified together and a connected Hub costs one run a minute, not one per login.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(60);

    public string Id => JobId;
    public string Title => "Ověření identit Hubu";
    public string Description => "Ověří účty přihlášené přes JetBrains Hub proti jeho adresáři a podle něj upraví jejich stav a členství ve skupinách.";
    public JobSchedule? Schedule => JobSchedule.FromData;

    public async Task<DateTime?> NextDueAsync(IServiceProvider services, CancellationToken ct)
    {
        var soonest = DateTime.UtcNow + Pause;
        var due = await synchronizer.NextDueAsync(ct);
        return due < soonest ? soonest : due;
    }

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var total = 0;
        IReadOnlyList<Guid> due;
        int verified;
        do
        {
            due = await synchronizer.DueAsync(ct);
            verified = 0;
            // Four consumers bound upstream pressure; database reservations coordinate other Atlas instances.
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (id, token) =>
            {
                try
                {
                    if (await synchronizer.VerifyAsync(id, false, token) is not null) Interlocked.Increment(ref verified);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    context.Log.LogWarning("Ověření identity Hubu se nepodařilo uložit ({ErrorType})", ex.GetType().Name);
                }
            });
            total += verified;
            context.Summarize(new { identities = total });
        }
        // A full list may have more behind it; one whose identities could not be reserved ends the pass.
        while (due.Count == HubSynchronizer.DueBatch && verified > 0);
    }
}

/// <summary>The Hub connection's health (class I): read every 5 s, logged when it changes. It only looks, so it stays a loop and
/// reports each pass to <see cref="SystemServiceHealth"/>.</summary>
public sealed class HubHealthWatch(HubSynchronizer synchronizer, SystemServiceHealth health, ILogger<HubHealthWatch> log) : BackgroundService
{
    public const string ServiceId = "hub.health";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        (Guid Connection, string State)? previousHealth = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var current = await synchronizer.HealthAsync(stoppingToken);
                if (current is not null)
                {
                    var currentHealth = (current.ConnectionId, current.State);
                    if (previousHealth is { } previous && previous != currentHealth)
                        log.LogInformation("Hub connection {ConnectionId} health changed from {PreviousState} to {State}; pending {Pending}, expired {Expired}, errors {Errors}",
                            current.ConnectionId, previous.State, current.State, current.Pending, current.Expired, current.Errors);
                    previousHealth = currentHealth;
                }
                health.Beat(ServiceId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning("Hub poll unavailable ({ErrorType})", ex.GetType().Name);
                health.Failed(ServiceId, ex);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
