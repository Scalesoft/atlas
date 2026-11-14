using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.Hub.Directory;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Hub.Persistence;
using NHibernate.Criterion;
using System.Diagnostics.CodeAnalysis;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Commands;

[Wolverine.Attributes.WolverineHandler]
public sealed class ApplyHubSyncHandler(ISession session, IAtlasLocks locks, AccessChanges accessChanges)
{
    private static readonly HashSet<string> s_failureCodes = new(StringComparer.Ordinal)
    {
        "unauthorized", "forbidden", "not-visible", "rate-limited", "timeout", "invalid-response", "response-too-large",
        "insufficient-permissions", "identity-mismatch", "expired-observation", "token-unavailable"
    };

    public async Task<HubSyncResult> Handle(ApplyHubSync command, CancellationToken ct)
    {
        var attempt = command.Attempt;
        // The exclusive content lock stops every content write of the wiki until this commit, so it is taken only for a change of access
        // (a membership added or closed, the account's effective Hub state) or a group's name. The usual verification only extends the
        // lease of access that stays as it is: the shared lock keeps it apart from administration, and no write it waits for or lets
        // through could be authorized by access this commit takes away. The plan is read first without locks, then again under them.
        var exclusive = command.Observation is { } first && await PlanUnlockedAsync(attempt, first, ct) is { Exclusive: true };
        session.Clear();
        if (exclusive) await locks.AcquireContentExclusiveAsync(ct);
        else await locks.AcquireContentSharedAsync(ct);
        await locks.AcquireAdvisoryAsync(HubReads.IdentityLock(attempt.ConnectionId, attempt.ExternalIdentityId), ct);
        var connection = await HubReads.CurrentAsync(session, ct);
        var identity = await session.GetAsync<ExternalIdentity>(attempt.ExternalIdentityId, ct);
        var state = await session.GetAsync<HubIdentityState>(attempt.ExternalIdentityId, ct);
        if (!MatchesAttempt(attempt, connection, identity, state))
            return new HubSyncResult(false, "superseded", 0, 0);
        var user = await session.GetAsync<UserAccount>(attempt.UserId, ct);
        if (user is null || !user.IsActive) return new HubSyncResult(false, "inactive-local-account", 0, 0);
        var now = await HubReads.NowAsync(session, ct);
        var rejected = RejectObservation(command, state, now);
        if (rejected is not null) return rejected;
        // RejectObservation only succeeds for a complete, timely observation of this exact identity.
        var plan = await PlanAsync(attempt, command.Observation!, connection, state, now, ct);
        // Access changed between the two reads (an administrator's binding meanwhile): the next pass applies it under the exclusive lock.
        if (plan.Exclusive && !exclusive) return new HubSyncResult(false, "superseded", 0, 0);
        return await ApplyObservationAsync(attempt, command.Observation!, plan, connection, state, now, ct);
    }

    /// <summary>The plan as the committed rows give it now, without locks and without writing anything.</summary>
    private async Task<Plan?> PlanUnlockedAsync(HubSyncAttempt attempt, HubUserObservation observation, CancellationToken ct)
    {
        var connection = await HubReads.CurrentAsync(session, ct);
        var identity = await session.GetAsync<ExternalIdentity>(attempt.ExternalIdentityId, ct);
        var state = await session.GetAsync<HubIdentityState>(attempt.ExternalIdentityId, ct);
        return MatchesAttempt(attempt, connection, identity, state)
            ? await PlanAsync(attempt, observation, connection, state, await HubReads.NowAsync(session, ct), ct)
            : null;
    }

    private static bool MatchesAttempt(HubSyncAttempt attempt, [NotNullWhen(true)] HubConnectionRevision? connection,
        ExternalIdentity? identity, [NotNullWhen(true)] HubIdentityState? state) =>
        connection is not null && connection.Id == attempt.RevisionId && connection.Mode != HubConnectionMode.Disabled &&
        identity is not null && identity.ActiveTo is null && identity.UserId == attempt.UserId &&
        identity.Issuer == connection.Issuer && identity.Subject == attempt.ExternalUserId &&
        state is not null && state.Generation == attempt.Generation && state.LastAttemptAt == attempt.StartedAt;

    private static HubSyncResult? RejectObservation(ApplyHubSync command, HubIdentityState state, DateTime now)
    {
        if (command.Observation is null)
        {
            RecordFailure(state, command.ErrorCode, command.RetryAfter, now);
            return new HubSyncResult(true, state.LastErrorCode!, 0, 0);
        }
        if (command.Attempt.StartedAt.AddMinutes(5) <= now)
        {
            RecordFailure(state, "expired-observation", null, now);
            return new HubSyncResult(false, "expired-observation", 0, 0);
        }
        if (!string.Equals(command.Observation.ExternalUserId, state.ExternalUserId, StringComparison.Ordinal))
        {
            RecordFailure(state, "identity-mismatch", null, now);
            return new HubSyncResult(false, "identity-mismatch", 0, 0);
        }
        return null;
    }

    /// <summary>What an observation changes; reads only.</summary>
    private sealed record Plan(bool Enforced, GroupMember[] Removed, Guid[] Added, IReadOnlyList<(UserGroup Group, string Name)> Renamed,
        bool EffectiveChanged)
    {
        public bool ChangesAccess => Enforced && (Removed.Length + Added.Length > 0 || EffectiveChanged);
        public bool Exclusive => ChangesAccess || Enforced && Renamed.Count > 0;
    }

    private async Task<Plan> PlanAsync(HubSyncAttempt attempt, HubUserObservation observation, HubConnectionRevision connection,
        HubIdentityState state, DateTime now, CancellationToken ct)
    {
        var bindings = await session.QueryOver<HubGroupBinding>().Where(x => x.ConnectionId == attempt.ConnectionId).ListAsync(ct);
        var memberships = await session.CreateQuery("from GroupMember m where m.UserId = :user and m.ActiveTo is null and m.GroupId in (select b.GroupId from HubGroupBinding b where b.ConnectionId = :connection)")
            .SetParameter("user", attempt.UserId).SetParameter("connection", attempt.ConnectionId).ListAsync<GroupMember>(ct);
        var observed = observation.Groups.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var expected = observation.State == HubDirectoryAccountState.Active
            ? bindings.Where(x => observed.ContainsKey(x.ExternalGroupId)).Select(x => x.GroupId).ToHashSet()
            : [];
        var removed = memberships.Where(x => !expected.Contains(x.GroupId)).ToArray();
        var existing = memberships.Select(x => x.GroupId).ToHashSet();
        var wasEffective = state.UpstreamAccountState == HubUpstreamAccountState.Active && state.VerifiedUntil > now && state.SnapshotStartedAt >= connection.ActiveFrom;
        return new Plan(connection.Mode == HubConnectionMode.Enforced, removed, expected.Except(existing).ToArray(),
            await RenamedGroupsAsync(bindings, observed, ct), wasEffective != (observation.State == HubDirectoryAccountState.Active));
    }

    private async Task<HubSyncResult> ApplyObservationAsync(HubSyncAttempt attempt, HubUserObservation observation, Plan plan,
        HubConnectionRevision connection, HubIdentityState state, DateTime now, CancellationToken ct)
    {
        IReadOnlyCollection<Guid> opened = [], closed = [];
        var before = state.UpstreamAccountState;
        if (plan.Enforced)
        {
            foreach (var member in plan.Removed) member.ActiveTo = Temporal.NextBoundary(member.ActiveFrom, now);
            closed = [.. plan.Removed.Select(x => x.Id)];
            opened = await AddMembershipAsync(attempt.UserId, plan.Added, now, ct);
            foreach (var (group, name) in plan.Renamed)
            {
                group.Name = name;
                await GroupHistory.RecordAsync(session, group, null, ct);
            }
            if (plan.ChangesAccess) accessChanges.Changed();
        }
        RecordSuccess(state, observation.State, attempt.StartedAt, now);
        // The synchronization has no actor; the audit names the Hub through the binding (AccessAuditView).
        await AccessAuditLog.OpenedAsync(session, AccessAuditLog.GroupMember, opened, null, ct);
        await HubHistory.IdentityAsync(session, state, before, null, ct);
        // Preview closes nothing, so it writes no membership.removed and revokes no share.
        await AccessAuditLog.ClosedAsync(session, AccessAuditLog.GroupMember, closed, null, ct);
        return new HubSyncResult(true, connection.Mode == HubConnectionMode.Preview ? "preview" : "verified", plan.Added.Length, plan.Removed.Length);
    }

    private async Task<IReadOnlyCollection<Guid>> AddMembershipAsync(Guid userId, Guid[] added, DateTime now, CancellationToken ct)
    {
        if (added.Length == 0) return [];
        var previousRows = await session.QueryOver<GroupMember>()
            .Where(x => x.UserId == userId)
            .And(GrantQueryLimits.InChunks(Projections.Property<GroupMember>(x => x.GroupId), added))
            .SelectList(list => list.SelectGroup(x => x.GroupId).SelectMax(x => x.ActiveTo)).ListAsync<object[]>(ct);
        var boundaries = previousRows.ToDictionary(x => (Guid)x[0], x => (DateTime?)x[1]);
        var ids = new List<Guid>();
        foreach (var groupId in added)
        {
            var previous = boundaries.GetValueOrDefault(groupId);
            ids.Add(Guid.NewGuid());
            await session.SaveAsync(new GroupMember
            {
                Id = ids[^1], GroupId = groupId, UserId = userId,
                ActiveFrom = previous is null ? now : Temporal.NextBoundary(previous.Value, now)
            }, ct);
        }
        return ids;
    }

    private static void RecordSuccess(HubIdentityState state, HubDirectoryAccountState observed, DateTime startedAt, DateTime now)
    {
        state.UpstreamAccountState = observed switch
        {
            HubDirectoryAccountState.Active => HubUpstreamAccountState.Active,
            HubDirectoryAccountState.Disabled => HubUpstreamAccountState.Disabled,
            _ => HubUpstreamAccountState.Missing
        };
        state.SnapshotStartedAt = startedAt;
        state.VerifiedUntil = startedAt.AddMinutes(5);
        state.LastSuccessAt = now;
        state.LastErrorCode = null;
        state.FailureCount = 0;
        state.NextAttemptAt = startedAt.AddSeconds(60);
    }

    /// <summary>The bound groups whose upstream name gives them another name.</summary>
    private async Task<IReadOnlyList<(UserGroup Group, string Name)>> RenamedGroupsAsync(IEnumerable<HubGroupBinding> bindings,
        IReadOnlyDictionary<string, HubDirectoryGroup> observed, CancellationToken ct)
    {
        var names = bindings.Where(x => observed.ContainsKey(x.ExternalGroupId)).ToDictionary(x => x.GroupId, x => observed[x.ExternalGroupId].Name);
        if (names.Count == 0) return [];
        var groups = await session.QueryOver<UserGroup>().Where(GrantQueryLimits.InChunks(Projections.Property<UserGroup>(x => x.Id), names.Keys.ToList())).ListAsync(ct);
        var renamed = new List<(UserGroup, string)>();
        foreach (var group in groups)
        {
            var name = await HubGroupNames.AvailableAsync(session, names[group.Id], group.Id, ct);
            if (name != group.Name) renamed.Add((group, name));
        }
        return renamed;
    }

    private static void RecordFailure(HubIdentityState state, string? code, TimeSpan? retryAfter, DateTime now)
    {
        state.LastErrorCode = code is not null && s_failureCodes.Contains(code) ? code : "unavailable";
        state.FailureCount = Math.Min(16, state.FailureCount + 1);
        var delay = TimeSpan.FromSeconds(Math.Min(3600, 60 * Math.Pow(2, state.FailureCount - 1)));
        if (retryAfter > delay) delay = retryAfter.Value > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : retryAfter.Value;
        state.NextAttemptAt = now.Add(delay);
    }
}
