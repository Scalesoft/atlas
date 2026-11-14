using System.Text.Json.Nodes;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.Vaults.Runtime;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Spaces.Commands;

/// <summary>
/// The permanent delete of a trashed space (unified trash spec §3 "Trvalé smazání"): only a wiki admin, who types its name; the web's
/// only. Every page of the space goes as a purged page (PagePurge: tombstones without content), with every board and link of its tree
/// (the modules' purge participants, a module switched off included) and every object of the space (contacts), its pins, image (files
/// after the commit), AI and storage settings, search logs and description.
/// Its grants and the grants on its vaults are closed (access history stays, audited as grant.removed); its vaults are archived with
/// their stored password erased and their secrets marked removed, so Atlas can open none of them again. The space itself stays as a
/// tombstone (Space.PurgedAt) that the access history names; one audit row (space.purged) keeps its name and the counts.
/// </summary>
public sealed class PurgeSpaceHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly ContentParticipants m_participants;
    private readonly AccessChanges m_changes;
    private readonly AttachmentFiles m_files;
    private readonly IServiceProvider m_services;
    private readonly ILogger<PurgeSpaceHandler> m_log;
    private readonly ObjectTypes m_types;

    public PurgeSpaceHandler(ISession session, IContentAccess access, IAtlasLocks locks, ContentParticipants participants, AccessChanges changes,
        AttachmentFiles files, IServiceProvider services, ObjectTypes types, ILogger<PurgeSpaceHandler> log)
    {
        m_types = types;
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_participants = participants;
        m_changes = changes;
        m_files = files;
        m_services = services;
        m_log = log;
    }

    public async Task Handle(PurgeSpace command, CancellationToken ct)
    {
        var actor = await TrashSpaceHandler.LockAsWikiAdminAsync(m_locks, m_access, m_session, command.SpaceId, ct);
        var space = await m_session.GetAsync<Space>(command.SpaceId, ct);
        if (space is not { TrashedAt: not null, PurgedAt: null })
        {
            throw new ApiException(404, "Prostor v koši nebyl nalezen.");
        }
        if (!string.Equals(command.Request.Name?.Trim(), space.Name, StringComparison.Ordinal))
        {
            throw new ApiException(400, "Pro potvrzení opište přesný název prostoru.", "space_name_mismatch");
        }
        var spaceId = space.Id;
        var ids = await m_session.QueryOver<Page>().Where(x => x.SpaceId == spaceId && x.PurgedAt == null)
            .OrderBy(x => x.Id).Asc.Select(x => x.Id).ListAsync<Guid>(ct);
        var pages = await PageSubtree.LockSelectionAsync(m_session, m_access, m_locks, ids.ToList(), AccessOperation.ManageAccess, ct);
        var vaults = await m_session.CreateSQLQuery("""SELECT "Id", "Name" FROM "Vault" WHERE "SpaceId" = :space ORDER BY lower("Name"), "Id" """)
            .SetParameter("space", spaceId).ListAsync<object[]>(ct);
        var detail = new JsonObject { ["pages"] = ids.Count };
        await ObjectPurge.ParticipantsAsync(m_participants, spaceId, ids.ToList(), null, actor.Id, detail, ct);
        var files = pages.Count == 0 ? [] : (await PagePurge.RunAsync(m_session, pages, actor.Id, ct)).ToList();
        // The other objects of the space (a contact of the space): as an object's purge, without an audit row each.
        // Only the types whose envelope is the object: a type of a disabled module keeps its rows, as everywhere (§5.5).
        var objects = await m_session.CreateSQLQuery("""
                SELECT "Id" FROM "ContentResource" WHERE "SpaceId" = :space AND "TypeId" = ANY(CAST(string_to_array(:types, ',') AS text[]))
                """)
            .AddScalar("Id", NHibernateUtil.Guid).SetParameter("space", spaceId).SetParameter("types", string.Join(',', m_types.StoredTypeIds()))
            .ListAsync<Guid>(ct);
        await ObjectPurge.ForgetAsync(m_session, objects.ToList(), actor.Id, ct);
        files.AddRange(await m_session.CreateSQLQuery("""SELECT "FileId" FROM "SpaceCover" WHERE "SpaceId" = :space AND "FileId" IS NOT NULL""")
            .AddScalar("FileId", NHibernateUtil.Guid).SetParameter("space", spaceId).ListAsync<Guid>(ct));
        // The space's grants and its vaults' grants end with it, audited as every closure.
        var spaceGrants = await m_session.CreateSQLQuery("""
            UPDATE "SpaceGrant" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond') WHERE "SpaceId" = :space AND "ActiveTo" IS NULL
            RETURNING "Id"
            """).SetParameter("space", spaceId).ListAsync<Guid>(ct);
        await AccessAuditLog.ClosedAsync(m_session, AccessAuditLog.SpaceGrant, [.. spaceGrants], actor.Id, ct);
        var vaultGrants = await m_session.CreateSQLQuery("""
            UPDATE "ResourceGrant" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
              WHERE "VaultSpaceId" = :space AND "ActiveTo" IS NULL
            RETURNING "Id"
            """).SetParameter("space", spaceId).ListAsync<Guid>(ct);
        await AccessAuditLog.ClosedAsync(m_session, AccessAuditLog.ResourceGrant, [.. vaultGrants], actor.Id, ct);
        await m_session.CreateSQLQuery($"""
            DELETE FROM "ContentResource" WHERE "Id" = {GuidList.Any(":objects")};
            UPDATE "Vault" SET "ArchivedAt" = COALESCE("ArchivedAt", now()), "ProtectedKdbxPassword" = NULL, "State" = 'locked' WHERE "SpaceId" = :space;
            UPDATE "VaultSecret" SET "RemovedAt" = COALESCE("RemovedAt", now()) WHERE "VaultId" IN (SELECT "Id" FROM "Vault" WHERE "SpaceId" = :space);
            DELETE FROM "SpacePin" WHERE "SpaceId" = :space;
            DELETE FROM "SpaceCover" WHERE "SpaceId" = :space;
            DELETE FROM "SpaceAiSetting" WHERE "SpaceId" = :space;
            DELETE FROM "SpaceTrashCleanupSetting" WHERE "SpaceId" = :space;
            DELETE FROM "StorageProfileSpace" WHERE "SpaceId" = :space;
            DELETE FROM "SearchGapResolution" WHERE "SpaceId" = :space;
            DELETE FROM "SearchQueryLog" WHERE "SpaceId" = :space;
            """).SetParameter("space", spaceId).SetParameter("objects", GuidList.Param(objects)).ExecuteUpdateAsync(ct);
        space.Description = "";
        space.PurgedAt = DateTime.UtcNow;
        await m_session.FlushAsync(ct);
        await m_session.CreateSQLQuery("""UPDATE "SpaceMetadataRevision" SET "Description" = '' WHERE "SpaceId" = :space""").SetParameter("space", spaceId)
            .ExecuteUpdateAsync(ct);
        detail["objects"] = objects.Count;
        detail["vaults"] = new JsonArray(vaults.Select(row => (JsonNode)JsonValue.Create((string)row[1])!).ToArray());
        await ObjectPurge.AuditAsync(m_session, spaceId, ObjectPurge.SpaceTypeId, ObjectScopes.Space, spaceId, space.Name, actor.Id, detail, ct);
        m_changes.Changed();
        m_changes.AfterCommit(() =>
        {
            PagePurge.RemoveFiles(m_files, files, m_log);
            // The decrypted keys held in memory go, as an archived vault's (VaultOperations.ArchiveAsync); a host without vaults holds none.
            if (m_services.GetService<VaultSnapshots>() is { } snapshots)
            {
                foreach (var row in vaults)
                {
                    snapshots.Forget((Guid)row[0]);
                }
            }
            return Task.CompletedTask;
        });
    }
}
