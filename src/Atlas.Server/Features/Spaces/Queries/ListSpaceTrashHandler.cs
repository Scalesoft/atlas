using Atlas.Server.Features.Spaces.Commands;
using Atlas.Server.Features.Spaces.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Spaces.Queries;

public sealed record ListSpaceTrash;

/// <summary>Správa wiki → Koš prostorů: the trashed spaces, newest first, with who trashed them, how many pages come back
/// and the vaults a permanent delete would destroy (its confirmation names them).</summary>
public sealed class ListSpaceTrashHandler
{
    private const int MaxSpaces = 200;
    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public ListSpaceTrashHandler(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<IReadOnlyList<TrashedSpaceItem>> Handle(ListSpaceTrash query, CancellationToken ct)
    {
        await new SpaceRoles(m_session).RequireWikiAdminAsync(await m_access.LockActorAsync(null, ct), ct, TrashSpaceHandler.WikiAdminOnly);
        var rows = await m_session.CreateSQLQuery("""
            SELECT s."Id", s."Name", s."Icon", s."Color", s."TrashedAt", s."TrashedById", u."DisplayName",
              CAST((SELECT count(*) FROM "Page" p WHERE p."SpaceId"=s."Id" AND p."TrashBatchId"=s."TrashBatchId" AND p."PurgedAt" IS NULL) AS integer),
              (SELECT string_agg(v."Name", chr(31) ORDER BY lower(v."Name"), v."Id") FROM "Vault" v WHERE v."SpaceId"=s."Id" AND v."ArchivedAt" IS NULL)
            FROM "Space" s JOIN "UserAccount" u ON u."Id"=s."TrashedById"
            WHERE s."TrashedAt" IS NOT NULL AND s."PurgedAt" IS NULL
            ORDER BY s."TrashedAt" DESC, s."Id" LIMIT :limit
            """).SetParameter("limit", MaxSpaces).ListAsync<object[]>(ct);
        return rows.Select(x => new TrashedSpaceItem((Guid)x[0], (string)x[1], (string)x[2], (string)x[3],
            DateTime.SpecifyKind((DateTime)x[4], DateTimeKind.Utc), (Guid)x[5], (string)x[6], (int)x[7],
            x[8] is string vaults ? vaults.Split((char)31) : [])).ToArray();
    }
}
