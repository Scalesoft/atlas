using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Hub.Persistence;
using Atlas.Server.Features.Hub.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Queries;

public sealed record FindHubLoginIdentity(Guid UserId, string Issuer, string Subject);
public sealed record ListDueHubIdentities;

/// <summary>When an identity is next due (HubIdentityJob's data schedule): now when <see cref="ListDueHubIdentities"/> lists one, else
/// the earliest NextAttemptAt of the verified ones; null without an enabled connection or identities.</summary>
public sealed record NextHubIdentityDue;
public sealed record GetHubHealth;

public sealed class HubIdentityQueriesHandler(ISession session)
{
    public async Task<HubHealthView?> Handle(GetHubHealth query, CancellationToken ct)
    {
        var config = await HubReads.CurrentAsync(session, ct);
        return config is null ? null : await HubSettingsReader.HealthAsync(session, config, ct);
    }

    public async Task<Guid?> Handle(FindHubLoginIdentity query, CancellationToken ct)
    {
        var connection = await HubReads.CurrentAsync(session, ct);
        if (connection is null || connection.Mode == HubConnectionMode.Disabled || connection.Issuer != query.Issuer) return null;
        var identity = await session.QueryOver<ExternalIdentity>()
            .Where(x => x.UserId == query.UserId && x.Issuer == query.Issuer && x.Subject == query.Subject && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        return identity?.Id;
    }

    public async Task<IReadOnlyList<Guid>> Handle(ListDueHubIdentities query, CancellationToken ct)
    {
        var connection = await HubReads.CurrentAsync(session, ct);
        if (connection is null || connection.Mode == HubConnectionMode.Disabled) return [];
        var now = await HubReads.NowAsync(session, ct);
        return (await session.CreateQuery("""
            select e.Id from ExternalIdentity e where e.ActiveTo is null and e.Issuer = :issuer
            and e.UserId in (select u.Id from UserAccount u where u.IsActive = true)
            and not exists (from HubIdentityState s where s.ExternalIdentityId = e.Id and s.ConnectionId = :connection
                and s.LastAttemptAt >= :revision and s.NextAttemptAt > :now)
            order by e.Id
            """).SetParameter("issuer", connection.Issuer).SetParameter("connection", connection.ConnectionId)
            .SetParameter("revision", connection.ActiveFrom).SetParameter("now", now).SetMaxResults(HubSynchronizer.DueBatch).ListAsync<Guid>(ct)).ToArray();
    }

    public async Task<DateTime?> Handle(NextHubIdentityDue query, CancellationToken ct)
    {
        if ((await Handle(new ListDueHubIdentities(), ct)).Count > 0) return await HubReads.NowAsync(session, ct);
        var connection = await HubReads.CurrentAsync(session, ct);
        if (connection is null || connection.Mode == HubConnectionMode.Disabled) return null;
        return await session.CreateQuery("""
            select min(s.NextAttemptAt) from HubIdentityState s, ExternalIdentity e
            where e.Id = s.ExternalIdentityId and e.ActiveTo is null and e.Issuer = :issuer
            and e.UserId in (select u.Id from UserAccount u where u.IsActive = true)
            and s.ConnectionId = :connection and s.LastAttemptAt >= :revision
            """).SetParameter("issuer", connection.Issuer).SetParameter("connection", connection.ConnectionId)
            .SetParameter("revision", connection.ActiveFrom).UniqueResultAsync<DateTime?>(ct);
    }
}
