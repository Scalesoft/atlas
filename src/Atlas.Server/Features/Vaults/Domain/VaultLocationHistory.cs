using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Domain;

/// <summary>
/// The history of a vault location (VaultLocationRevision), written in the transaction of the change with the actor the caller passes.
/// The protected password is not copied: a revision records whether one is stored and whether this change replaced it. A writer calls
/// <see cref="RecordAsync"/> after changing the location and before the session flushes it. Was TR_VaultLocation_History until 3.6
/// (migration 2026110942); SecurityAuditPinTests keeps the rows as the trigger wrote them.
/// </summary>
public static class VaultLocationHistory
{
    public static async Task RecordAsync(ISession session, VaultLocation location, Guid? actorId, CancellationToken ct)
    {
        var changed = session.ChangedProperties(location);
        if (changed is { Count: 0 })
        {
            return;
        }
        await session.FlushAsync(ct);
        var boundary = await session.CreateSQLQuery("""
            UPDATE "VaultLocationRevision" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
            WHERE "LocationId" = :id AND "ActiveTo" IS NULL
            RETURNING "ActiveTo"
            """).SetParameter("id", location.Id).UniqueResultAsync<DateTime?>(ct);
        await session.CreateSQLQuery("""
            INSERT INTO "VaultLocationRevision"("Id","LocationId","Name","Kind","Root","Username","Domain","HasPassword","PasswordChanged",
                "ArchivedAt","ChangedBy","ActiveFrom","ActiveTo")
            SELECT :revision, l."Id", l."Name", l."Kind", l."Root", l."Username", l."Domain", l."ProtectedPassword" IS NOT NULL, :replaced,
                l."ArchivedAt", :actor, COALESCE(:boundary, clock_timestamp()), NULL
            FROM "VaultLocation" l WHERE l."Id" = :id
            """)
            .SetParameter("revision", Guid.NewGuid())
            .SetParameter("id", location.Id)
            .SetParameter("replaced", changed?.Contains(nameof(VaultLocation.ProtectedPassword)) == true)
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime)
            .ExecuteUpdateAsync(ct);
    }
}
