using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Domain;

/// <summary>
/// The Hub's audit facts (HubChange, each with its AccessAuditEvent) and the history of its group bindings (HubGroupBindingRevision),
/// written in the transaction of the change with the actor the caller passes (null: the synchronization). Writers call these after
/// changing the entity and before the session flushes it. Was TR_HubConnectionRevision_Audit, TR_HubGroupBinding_History,
/// TR_HubGroupBindingRevision_Audit, TR_HubIdentityState_Audit and the issuer guard until 3.6 (migration 2026110943); HubAuditPinTests
/// keeps the rows as the triggers wrote them. The identity rules of bindings and identity states are IntegrityRules'.
/// </summary>
public static class HubHistory
{
    /// <summary>A new connection revision (the caller saved it, not flushed): connection.created or connection.updated, by its author.
    /// A connection keeps its issuer: a revision of another issuer is refused, under the connection's row lock.</summary>
    public static async Task ConnectionAsync(ISession session, HubConnectionRevision revision, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        // Revisions of one connection are serialized on its identity row, the first one included.
        await session.GetForUpdateAsync<HubConnection>(revision.ConnectionId, ct);
        var issuers = await session.QueryOver<HubConnectionRevision>().Where(x => x.ConnectionId == revision.ConnectionId && x.Id != revision.Id)
            .Select(x => x.Issuer).ListAsync<string>(ct);
        if (issuers.Any(issuer => issuer != revision.Issuer))
        {
            throw new InvalidOperationException("A Hub connection cannot change issuer");
        }
        await RecordAsync(session, revision.ChangedById, "hub.connection.changed", """
            INSERT INTO "HubChange"("ConnectionId","ConnectionRevisionId","Reason") VALUES (:connection, :revision, :reason) RETURNING "Id"
            """, query => query.SetParameter("connection", revision.ConnectionId).SetParameter("revision", revision.Id)
                .SetParameter("reason", issuers.Count > 0 ? "connection.updated" : "connection.created"), ct);
    }

    /// <summary>A new or changed binding: its revision and binding.attached or binding.updated. Nothing when it did not change.</summary>
    public static async Task BindingAsync(ISession session, HubGroupBinding binding, Guid? actorId, CancellationToken ct)
    {
        // Compared with the current revision, not the session: a flush in between loses nothing.
        await session.FlushAsync(ct);
        if (await session.CreateSQLQuery("""
                SELECT EXISTS (SELECT 1 FROM "HubGroupBindingRevision" r JOIN "HubGroupBinding" b ON b."GroupId" = r."GroupId"
                    WHERE r."GroupId" = :group AND r."ActiveTo" IS NULL
                      AND (r."ConnectionId", r."ExternalGroupId", r."Enabled", r."GrantsWikiAdmin") = (b."ConnectionId", b."ExternalGroupId", b."Enabled", b."GrantsWikiAdmin"))
                """).SetParameter("group", binding.GroupId).UniqueResultAsync<bool>(ct))
        {
            return;
        }
        var boundary = await session.CreateSQLQuery("""
            UPDATE "HubGroupBindingRevision" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
            WHERE "GroupId" = :group AND "ActiveTo" IS NULL
            RETURNING "ActiveTo"
            """).SetParameter("group", binding.GroupId).UniqueResultAsync<DateTime?>(ct);
        var revisionId = Guid.NewGuid();
        await session.CreateSQLQuery("""
            INSERT INTO "HubGroupBindingRevision"("Id","GroupId","ConnectionId","ExternalGroupId","Enabled","GrantsWikiAdmin","ChangedBy","ActiveFrom","ActiveTo")
            SELECT :revision, b."GroupId", b."ConnectionId", b."ExternalGroupId", b."Enabled", b."GrantsWikiAdmin", :actor,
                COALESCE(:boundary, clock_timestamp()), NULL
            FROM "HubGroupBinding" b WHERE b."GroupId" = :group
            """)
            .SetParameter("revision", revisionId).SetParameter("group", binding.GroupId)
            .SetParameter("actor", actorId, NHibernateUtil.Guid).SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime)
            .ExecuteUpdateAsync(ct);
        await RecordAsync(session, actorId, "hub.binding.changed", """
            INSERT INTO "HubChange"("ConnectionId","GroupId","BindingRevisionId","Reason") VALUES (:connection, :group, :revision, :reason) RETURNING "Id"
            """, query => query.SetParameter("connection", binding.ConnectionId).SetParameter("group", binding.GroupId)
                .SetParameter("revision", revisionId).SetParameter("reason", boundary is null ? "binding.attached" : "binding.updated"), ct);
    }

    /// <summary>An observed identity whose upstream account state changed from <paramref name="previous"/> (null: a new state, audited
    /// unless unverified): identity.observed with the previous and current state.</summary>
    public static async Task IdentityAsync(ISession session, HubIdentityState state, HubUpstreamAccountState? previous, Guid? actorId,
        CancellationToken ct)
    {
        if (state.UpstreamAccountState == (previous ?? HubUpstreamAccountState.Unverified))
        {
            return;
        }
        await session.FlushAsync(ct);
        await RecordAsync(session, actorId, "hub.identity.changed", """
            INSERT INTO "HubChange"("ConnectionId","UserId","ExternalIdentityId","ExternalUserId","PreviousState","CurrentState","Reason")
            VALUES (:connection, :user, :identity, :subject, :previous, :current, 'identity.observed') RETURNING "Id"
            """, query => query.SetParameter("connection", state.ConnectionId).SetParameter("user", state.UserId)
                .SetParameter("identity", state.ExternalIdentityId).SetParameter("subject", state.ExternalUserId)
                .SetParameter("previous", (short?)previous, NHibernateUtil.Int16).SetParameter("current", (short)state.UpstreamAccountState), ct);
    }

    private static async Task RecordAsync(ISession session, Guid? actorId, string action, string insert, Func<ISQLQuery, IQuery> parameters,
        CancellationToken ct)
    {
        var change = await parameters(session.CreateSQLQuery(insert)).UniqueResultAsync<long>(ct);
        await AccessAuditLog.RecordAsync(session, actorId, action, AccessAuditLog.HubChange, change, ct);
    }
}
