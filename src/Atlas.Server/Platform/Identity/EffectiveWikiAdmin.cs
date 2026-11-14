using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>Independent administrator sources. Hub authority is never written onto the account.</summary>
public sealed record WikiAdminSources(bool Local, string? OidcIssuer, DateTime? HubVerifiedUntil)
{
    public bool Persisted => Local || OidcIssuer is not null;

    public (bool Allowed, DateTime? ExpiresAt) Authority(DateTime now) => Persisted
        ? (true, null)
        : HubVerifiedUntil is { } until && now < until ? (true, until) : (false, null);
}

/// <summary>Reads live Hub authority for an explicit account, including background and collaboration actors.</summary>
public static class EffectiveWikiAdmin
{
    public static async Task<WikiAdminSources> ReadAsync(ISession session, UserAccount actor, CancellationToken ct)
    {
        if (!actor.IsActive) return new(false, null, null);
        var query = session.CreateSQLQuery("SELECT \"HubVerifiedUntil\" FROM \"EffectiveWikiAdmin\" WHERE \"UserId\"=:user")
            .AddScalar("HubVerifiedUntil", NHibernateUtil.UtcDateTime);
        foreach (var table in new[] { "GroupMember", "HubGroupBinding", "HubIdentityState", "HubConnectionRevision", "ExternalIdentity", "UserAccount" })
            query.AddSynchronizedQuerySpace(table);
        var until = await query.SetParameter("user", actor.Id).UniqueResultAsync<DateTime?>(ct);
        return new(actor.IsWikiAdmin && actor.OidcAdminIssuer is null, actor.IsWikiAdmin ? actor.OidcAdminIssuer : null, until);
    }

    public static async Task<bool> IsAdminAsync(ISession session, UserAccount actor, CancellationToken ct) =>
        (await ReadAsync(session, actor, ct)).Authority(DateTime.UtcNow).Allowed;
}
