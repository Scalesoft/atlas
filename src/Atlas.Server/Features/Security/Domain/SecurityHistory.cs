using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Domain;

/// <summary>
/// The history of the security configuration (SecurityConfigurationRevision) and of trusted addresses (TrustedAddressRevision) with
/// their audit, written in the transaction of the change with the actor the caller passes. A writer calls these after changing the
/// entity and before the session flushes it. Was TR_SecurityConfiguration_History, TR_TrustedAddress_History and their audit triggers
/// until 3.6 (migration 2026110942); SecurityAuditPinTests keeps the rows as the triggers wrote them. The version rules themselves
/// (a change increments Version by one) are IntegrityRules'.
/// </summary>
public static class SecurityHistory
{
    /// <summary>A new configuration version: its revision, and security.proxy_hops_changed when the hop limit differs from the
    /// previous version's. Nothing when Version did not change.</summary>
    public static async Task ConfigurationAsync(ISession session, SecurityConfiguration configuration, Guid? actorId, CancellationToken ct)
    {
        if (session.ChangedProperties(configuration) is { } changed && !changed.Contains(nameof(SecurityConfiguration.Version)))
        {
            return;
        }
        await session.FlushAsync(ct);
        var previous = await session.CreateSQLQuery("""
            UPDATE "SecurityConfigurationRevision" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
            WHERE "ActiveTo" IS NULL
            RETURNING "ActiveTo", "Version", "ProxyHopLimit"
            """).UniqueResultAsync<object[]>(ct);
        var revisionId = Guid.NewGuid();
        await session.CreateSQLQuery("""
            INSERT INTO "SecurityConfigurationRevision"("Id","Version","ProxyHopLimit","ChangedBy","ActiveFrom","ActiveTo")
            VALUES (:id, :version, :hops, :actor, COALESCE(:boundary, clock_timestamp()), NULL)
            """)
            .SetParameter("id", revisionId)
            .SetParameter("version", configuration.Version)
            .SetParameter("hops", configuration.ProxyHopLimit)
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .SetParameter("boundary", previous?[0], NHibernateUtil.UtcDateTime)
            .ExecuteUpdateAsync(ct);
        if (previous is not null && Convert.ToInt64(previous[1]) == configuration.Version - 1 && Convert.ToInt16(previous[2]) != configuration.ProxyHopLimit)
        {
            await AccessAuditLog.RecordAsync(session, actorId, "security.proxy_hops_changed", AccessAuditLog.SecurityRevision, revisionId, ct);
        }
    }

    /// <summary>A new or changed trusted address: its revision, with the configuration version the change activated (flush the new
    /// version first), and trusted_network.* or trusted_proxy.* created, updated or removed (archived). Nothing when it did not change.</summary>
    public static async Task AddressAsync(ISession session, TrustedAddress address, Guid? actorId, CancellationToken ct)
    {
        if (session.ChangedProperties(address) is { Count: 0 })
        {
            return;
        }
        await session.FlushAsync(ct);
        var boundary = await session.CreateSQLQuery("""
            UPDATE "TrustedAddressRevision" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
            WHERE "AddressId" = :id AND "ActiveTo" IS NULL
            RETURNING "ActiveTo"
            """).SetParameter("id", address.Id).UniqueResultAsync<DateTime?>(ct);
        var revisionId = Guid.NewGuid();
        await session.CreateSQLQuery("""
            INSERT INTO "TrustedAddressRevision"("Id","AddressId","Purpose","Name","Kind","Range","IsActive","ArchivedAt","ConfigurationVersion",
                "ChangedBy","ActiveFrom","ActiveTo")
            SELECT :revision, a."Id", a."Purpose", a."Name", a."Kind", a."Range", a."IsActive", a."ArchivedAt",
                (SELECT c."Version" FROM "SecurityConfiguration" c WHERE c."Id" = 1), :actor, COALESCE(:boundary, clock_timestamp()), NULL
            FROM "TrustedAddress" a WHERE a."Id" = :id
            """)
            .SetParameter("revision", revisionId)
            .SetParameter("id", address.Id)
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime)
            .ExecuteUpdateAsync(ct);
        var action = (address.Purpose == TrustedAddress.NetworkPurpose ? "trusted_network." : "trusted_proxy.")
            + (boundary is null ? "created" : address.ArchivedAt is not null ? "removed" : "updated");
        await AccessAuditLog.RecordAsync(session, actorId, action, AccessAuditLog.TrustedAddressRevision, revisionId, ct);
    }
}
