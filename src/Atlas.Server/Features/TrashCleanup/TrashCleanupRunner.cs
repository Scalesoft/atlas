using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.TrashCleanup.Commands;
using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Features.TrashCleanup.Contracts;
using Atlas.Server.Features.TrashCleanup.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.TrashCleanup;

/// <summary>
/// The cleanup of orphaned items (unified trash spec §4b) in one place: every item trashed longer than the days goes for good through
/// the object path's purge (PurgeObject: the type's own locks, rights, audit and Git delete commit), then every object nobody uses,
/// unchanged as long, goes to the trash (TrashObject), so it is gone for good only a cleanup later. Each item is its own short
/// transaction; one a right or someone's unsaved live edit refuses is left for the next run. Runs as the account in effect: the
/// signed-in admin for „Vyčistit teď“, the admin who switched the rule on for the daily run (ActingAccount).
/// </summary>
public sealed class TrashCleanupRunner(IServiceScopeFactory scopes, ILogger<TrashCleanupRunner> log)
{
    public async Task<TrashCleanupResult> RunAsync(Guid? spaceId, int days, DateTime now, CancellationToken ct)
    {
        var before = now.AddDays(-days);
        var (trashed, unused) = await ReadAsync(async (session, services) =>
        {
            var types = services.GetRequiredService<ObjectTypes>();
            return (await TrashCleanupCandidates.TrashedAsync(session, spaceId, types.TypeIds(), before, ct),
                await TrashCleanupCandidates.UnusedAsync(session, services.GetRequiredService<ContentParticipants>(), spaceId, types.StoredTypeIds(), before, ct));
        });
        int purged = 0, moved = 0, skipped = 0;
        foreach (var id in trashed)
        {
            // Asked again right before: a batch's top page took its subpages along, and an item restored and trashed again meanwhile is
            // young again.
            if (!await ReadAsync((session, _) => StillDueAsync(session, id, before, ct)))
            {
                continue;
            }
            if (await TryAsync(new PurgeObject(id), ct)) purged++; else skipped++;
        }
        foreach (var id in unused)
        {
            // Edited, placed or mentioned since the list was read: it stays.
            var still = await ReadAsync(async (session, services) => (await TrashCleanupCandidates.UnusedAsync(session,
                services.GetRequiredService<ContentParticipants>(), spaceId, services.GetRequiredService<ObjectTypes>().StoredTypeIds(), before, ct, [id])).Count > 0);
            if (!still)
            {
                continue;
            }
            if (await TryAsync(new TrashObject(id), ct)) moved++; else skipped++;
        }
        return new TrashCleanupResult(purged, moved, skipped);
    }

    /// <summary>The daily run: every place whose rule in effect is automatic, as the account that switched it on.</summary>
    public async Task<int> RunDueAsync(DateTime now, CancellationToken ct)
    {
        var places = await ReadAsync(async (session, _) =>
        {
            var wiki = await CurrentTrashCleanup.LoadWikiAsync(session, ct);
            var spaces = await session.QueryOver<Space>().Where(x => x.TrashedAt == null && x.PurgedAt == null).Select(x => x.Id).ListAsync<Guid>(ct);
            var result = new List<(Guid? Space, TrashCleanupRule Rule)>();
            foreach (var space in spaces)
            {
                result.Add((space, CurrentTrashCleanup.Rule(wiki, await CurrentTrashCleanup.LoadSpaceAsync(session, space, ct), true)));
            }
            result.Add((null, CurrentTrashCleanup.Rule(wiki, null, false)));
            var actors = result.Where(x => x.Rule.ActorId is not null).Select(x => x.Rule.ActorId!.Value).Distinct().ToArray();
            var stamps = new Dictionary<Guid, string?>();
            foreach (var actor in actors)
            {
                if (await session.GetAsync<UserAccount>(actor, ct) is { IsActive: true } account)
                {
                    stamps[actor] = account.SecurityStamp;
                }
            }
            return result.Where(x => x.Rule.ActorId is { } id && stamps.ContainsKey(id)).Select(x => (x.Space, x.Rule, Stamp: stamps[x.Rule.ActorId!.Value])).ToArray();
        });
        var done = 0;
        foreach (var (space, rule, stamp) in places)
        {
            using (ActingAccount.Use(rule.ActorId!.Value, stamp))
            {
                // Who switched it on must still hold the role behind it, read as for their own request: a wiki admin for a rule for all, an
                // admin of the space for its own. Otherwise the place waits until an admin saves the rule again.
                if (!await TryAsync(rule.DecidedByWiki ? new GetWikiTrashCleanup() : new GetSpaceTrashCleanup(space!.Value), ct))
                {
                    log.LogWarning("Trash cleanup of {Place} skipped: the account that switched it on no longer administers it", space?.ToString() ?? "wiki");
                    continue;
                }
                try
                {
                    var result = await RunAsync(space, rule.Days, now, ct);
                    if (result.Purged + result.Trashed + result.Skipped > 0)
                    {
                        log.LogInformation("Trash cleanup of {Place} purged {Purged}, trashed {Trashed} unused and left {Skipped} items",
                            space?.ToString() ?? "wiki", result.Purged, result.Trashed, result.Skipped);
                    }
                    done += result.Purged + result.Trashed;
                }
                catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // One place's failure (its candidates could not be read) leaves the others to run.
                    log.LogError(failure, "Trash cleanup of {Place} failed", space?.ToString() ?? "wiki");
                }
            }
        }
        return done;
    }

    private static async Task<bool> StillDueAsync(ISession session, Guid id, DateTime before, CancellationToken ct)
        => await session.CreateSQLQuery("""
                SELECT EXISTS (SELECT 1 FROM "ContentResource" r LEFT JOIN "Page" p ON p."Id" = r."Id"
                               WHERE r."Id" = :id AND r."TrashedAt" < :before AND p."PurgedAt" IS NULL)
                """).SetParameter("id", id).SetParameter("before", before, NHibernate.NHibernateUtil.UtcDateTime).UniqueResultAsync<bool>(ct);

    private async Task<bool> TryAsync(object command, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(command, ct);
            return true;
        }
        catch (ApiException failure)
        {
            log.LogInformation("Trash cleanup left {Command}: {Status} {Code}", command, failure.Status, failure.Code);
            return false;
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A deadlock or a module's fault on one item must not hold up every item after it, day after day.
            log.LogWarning(failure, "Trash cleanup left {Command}", command);
            return false;
        }
    }

    private async Task<T> ReadAsync<T>(Func<ISession, IServiceProvider, Task<T>> read)
    {
        using var scope = scopes.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ISession>(), scope.ServiceProvider);
    }
}

/// <summary>Runs <see cref="TrashCleanupRunner.RunDueAsync"/> every night, as the admin who switched each rule on (tests call the runner). The
/// time is a cron plan, so a restart no longer moves it; a night the server was down runs once at the next start.</summary>
public sealed class TrashCleanupJob(TrashCleanupRunner runner, TimeProvider clock) : IJobDefinition
{
    public string Id => "trash.cleanup";

    public string Title => "Úklid koše";

    public string Description => "Podle automatických pravidel prostorů a wiki smaže natrvalo položky dlouho v koši a nepoužívané objekty přesune do koše.";

    public JobSchedule? Schedule => JobSchedule.Cron("30 1 * * *");

    public bool RunsWhenQuiet => true;

    public async Task RunAsync(JobContext context, CancellationToken ct)
        => context.Summarize(new { done = await runner.RunDueAsync(clock.GetUtcNow().UtcDateTime, ct) });
}
