using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Hub.Persistence;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Commands;

// Wolverine's culture-sensitive suffix scan treats cH in SyncHandler as the Czech letter ch.
[Wolverine.Attributes.WolverineHandler]
public sealed class ReserveHubSyncHandler(ISession session, IAtlasLocks locks, IJobRequests jobs)
{
    public async Task<HubSyncAttempt?> Handle(ReserveHubSync command, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        var connection = await HubReads.CurrentAsync(session, ct);
        if (connection is null || connection.Mode == HubConnectionMode.Disabled) return null;
        await locks.AcquireAdvisoryAsync(HubReads.IdentityLock(connection.ConnectionId, command.ExternalIdentityId), ct);
        var identity = await FindEligibleIdentityAsync(connection, command.ExternalIdentityId, ct);
        if (identity is null) return null;
        var now = await HubReads.NowAsync(session, ct);
        var state = await GetOrCreateStateAsync(connection, identity, ct);
        if (state.ConnectionId != connection.ConnectionId || state.ExternalUserId != identity.Subject) return null;
        var sameConfiguration = state.LastAttemptAt >= connection.ActiveFrom;
        if (ShouldWait(state, sameConfiguration, command.Force, now)) return null;
        state.Generation = checked(state.Generation + 1);
        state.LastAttemptAt = now;
        state.NextAttemptAt = now.AddSeconds(60);
        if (!sameConfiguration) state.FailureCount = 0;
        return new HubSyncAttempt(identity.Id, identity.UserId, connection.ConnectionId, connection.Id,
            state.Generation, now, state.ExternalUserId, connection.BaseUrl, connection.ProtectedToken);
    }

    private async Task<ExternalIdentity?> FindEligibleIdentityAsync(HubConnectionRevision connection, Guid identityId, CancellationToken ct)
    {
        var identity = await session.GetAsync<ExternalIdentity>(identityId, ct);
        if (identity is null || identity.ActiveTo is not null || identity.Issuer != connection.Issuer) return null;
        await locks.AcquireAdvisoryAsync($"hub-user:{connection.ConnectionId:N}:{identity.UserId:N}", ct);
        var user = await session.GetAsync<UserAccount>(identity.UserId, ct);
        if (user is null || !user.IsActive) return null;
        return identity;
    }

    private async Task<HubIdentityState> GetOrCreateStateAsync(HubConnectionRevision connection, ExternalIdentity identity, CancellationToken ct)
    {
        var state = await session.GetAsync<HubIdentityState>(identity.Id, ct);
        if (state is not null) return state;
        // A closed link can be replaced, but never merge active identities by name or email.
        var previousStates = await session.QueryOver<HubIdentityState>()
            .Where(x => x.ConnectionId == connection.ConnectionId && (x.UserId == identity.UserId || x.ExternalUserId == identity.Subject)).ListAsync(ct);
        foreach (var previous in previousStates)
        {
            var oldIdentity = await session.GetAsync<ExternalIdentity>(previous.ExternalIdentityId, ct);
            if (oldIdentity?.ActiveTo is null) throw new ApiException(409, "Účet už používá jinou aktivní identitu tohoto Hubu.");
            await session.DeleteAsync(previous, ct);
            await session.FlushAsync(ct);
        }
        state = new HubIdentityState
        {
            ExternalIdentityId = identity.Id, UserId = identity.UserId, ConnectionId = connection.ConnectionId,
            ExternalUserId = identity.Subject
        };
        await session.SaveAsync(state, ct);
        // The job plans from the states: one whose last pass found none (a Hub without logins yet) has no plan until this one.
        await jobs.RescheduleAsync(HubIdentityJob.JobId, ct);
        return state;
    }

    private static bool ShouldWait(HubIdentityState state, bool sameConfiguration, bool force, DateTime now) =>
        sameConfiguration && state.NextAttemptAt > now && (!force || state.FailureCount > 0);
}
