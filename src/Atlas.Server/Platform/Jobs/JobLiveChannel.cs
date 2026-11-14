using Atlas.Server.Platform.Live;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Jobs;

/// <summary>
/// The runs of background jobs on /hubs/live, as the jobs pages show them (JobAccess, 3.3 plan §4.6): <c>jobs:{Guid.Empty}</c> every
/// run, for an effective wiki administrator (Hub's included); <c>jobs:{spaceId}</c> the runs of that space, for its administrators. The
/// server derives the channels (<see cref="IDerivedLiveChannel"/>), so a space administrator never hears another space's run or a
/// global one. <see cref="JobRunChanged"/> carries no error, log or summary: it only says "read the run again" over HTTP.
/// </summary>
public sealed class JobLiveChannel(AtlasDatabase database) : IDerivedLiveChannel
{
    public const string Prefix = "jobs";

    public const string Event = "JobRunChanged";

    public string Kind => Prefix;

    public static string Name(Guid resourceId) => Prefix + ":" + resourceId.ToString("D");

    public async Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct)
    {
        return (await AuthorizeManyAsync(subscriber, [resourceId], ct))[resourceId];
    }

    /// <summary>All of the account's job channels in one decision (the same queries for 1 space or 50): the session proof, the wiki
    /// administration and the administered spaces, as JobAccess decides the jobs pages. A trashed space administers nothing.</summary>
    public async Task<IReadOnlyDictionary<Guid, LiveAccess>> AuthorizeManyAsync(LiveSubscriber subscriber, IReadOnlyCollection<Guid> resourceIds, CancellationToken ct)
    {
        using ISession session = database.OpenSession();
        var allowed = await AllowedAsync(session, subscriber, ct);
        return resourceIds.Distinct().ToDictionary(id => id, id => allowed is null ? LiveAccess.Forbidden
            : allowed.Contains(id) ? new LiveAccess(LiveAccessOutcome.Allowed, false, 0) : LiveAccess.NotFound);
    }

    public async Task<IReadOnlyList<Guid>> ResourcesAsync(LiveSubscriber subscriber, CancellationToken ct)
    {
        using ISession session = database.OpenSession();
        return [.. await AllowedAsync(session, subscriber, ct) ?? []];
    }

    /// <summary>{Guid.Empty} for an effective wiki administrator, else the administered spaces; null when the session no longer proves
    /// the account.</summary>
    private static async Task<HashSet<Guid>?> AllowedAsync(ISession session, LiveSubscriber subscriber, CancellationToken ct)
    {
        if (await UserLiveChannel.ProvenAsync(session, subscriber, ct) is not { } actor)
        {
            return null;
        }
        if (await EffectiveWikiAdmin.IsAdminAsync(session, actor, ct))
        {
            return [Guid.Empty];
        }
        return [.. (await new SpaceRoles(session).EffectiveAsync(actor, ct)).Where(space => space.Value == SpaceRole.Admin).Select(space => space.Key)];
    }

    /// <summary>To the wiki administrators, and to the run's space when it has one.</summary>
    public static async Task PublishAsync(LiveRelay relay, JobRunChanged message)
    {
        await relay.PublishAsync(Name(Guid.Empty), Event, message);
        if (message.SpaceId is { } space)
        {
            await relay.PublishAsync(Name(space), Event, message);
        }
    }
}

/// <summary>Server → client on <c>jobs:</c>: a run started, reached a step, was put back for a retry or ended.</summary>
public sealed record JobRunChanged(long RunId, string JobId, string State, Guid? SpaceId);
