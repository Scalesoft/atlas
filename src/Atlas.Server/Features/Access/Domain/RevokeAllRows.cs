using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Access.Domain;

/// <summary>What revoke-all closes, counted the way the command closes it, so the preview and the change agree: the account's own
/// current space grants and page, vault and secret grants (group grants stay with the group), its current local group memberships and its
/// personal API tokens that could still authenticate.</summary>
public static class RevokeAllRows
{
    private const string LocalMemberships = """
        m."UserId" = :user AND m."ActiveTo" IS NULL
        AND NOT EXISTS (SELECT 1 FROM "HubGroupBinding" b WHERE b."GroupId" = m."GroupId")
        """;

    public static async Task<RevokeAllImpact> ImpactAsync(ISession session, Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var spaceGrants = await session.QueryOver<SpaceGrant>().Where(g => g.UserId == userId && g.ActiveTo == null).RowCountAsync(ct);
        var resourceGrants = await session.QueryOver<ResourceGrant>().Where(g => g.UserId == userId && g.ActiveTo == null).RowCountAsync(ct);
        var groups = await session.CreateSQLQuery($"SELECT CAST(count(*) AS int) FROM \"GroupMember\" m WHERE {LocalMemberships}")
            .SetParameter("user", userId).UniqueResultAsync<int>(ct);
        var tokens = await session.QueryOver<PersonalApiToken>().Where(t => t.UserId == userId && t.ActiveTo == null && t.ExpiresAt > now)
            .RowCountAsync(ct);
        return new RevokeAllImpact(spaceGrants + resourceGrants, groups, tokens);
    }

    /// <summary>The ids of the memberships it closed, for their audit.</summary>
    internal static async Task<IReadOnlyCollection<Guid>> CloseLocalMembershipsAsync(ISession session, Guid userId, CancellationToken ct) =>
        [.. await session.CreateSQLQuery($"""
            UPDATE "GroupMember" m SET "ActiveTo" = greatest(clock_timestamp(), m."ActiveFrom" + interval '1 microsecond')
            WHERE {LocalMemberships}
            RETURNING m."Id"
            """).SetParameter("user", userId).ListAsync<Guid>(ct)];
}
