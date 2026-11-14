using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Queries;

public sealed class ListPageTrashHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly OrganizationLocks m_locks;
    private readonly ContentParticipants m_participants;

    public ListPageTrashHandler(ISession session, IContentAccess access, OrganizationLocks locks, ContentParticipants participants)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_participants = participants;
    }

    public async Task<PageTrashDto> Handle(ListPageTrash query, CancellationToken ct)
    {
        if (query.Offset < 0 || query.Offset > 100_000 || query.Limit < 1 || query.Limit > 100)
        {
            throw new ApiException(400, "Neplatné stránkování koše.");
        }
        var spaceId = query.SpaceId;
        var actor = await m_locks.LockForReadAsync(spaceId, ct);
        await m_access.RequireSpaceAsync(spaceId, SpaceRole.Admin, ct);
        var entries = m_session.QueryOver<PageTreeEntry>().Where(x => x.SpaceId == spaceId && x.IsTrashed && !x.IsPurged);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            var actorId = actor.Id;
            entries = entries.And(x => x.IsPublished || x.OwnerId == actorId);
        }
        // With other content types in the trash, each source gives its first offset + limit + 1 and the merge pages them.
        var others = m_participants.Tree;
        var window = query.Offset + query.Limit + 1;
        var rows = await entries.OrderBy(x => x.TrashedAt).Desc.ThenBy(x => x.Id).Asc
            .Skip(others.Count == 0 ? query.Offset : 0).Take(others.Count == 0 ? query.Limit + 1 : window).ListAsync(ct);
        // TrashedAt/TrashedById/TrashBatchId are non-null for every trashed row (CK_Page_Trash).
        var all = rows.Select(x => new TrashedPageItem(x.Id, x.ParentId, x.WorkingTitle, x.Version, x.TrashedAt!.Value, x.TrashedById!.Value, x.TrashBatchId!.Value))
            .ToList();
        if (others.Count > 0)
        {
            foreach (var participant in others)
            {
                all.AddRange((await participant.TrashedAsync(spaceId, window, ct)).Select(x =>
                    new TrashedPageItem(x.Id, x.ParentId, x.Title, 0, x.TrashedAt, x.TrashedById, x.TrashBatchId) { Type = x.Type }));
            }
            all = all.OrderByDescending(x => x.TrashedAt).ThenBy(x => x.Id.ToString("N"), StringComparer.Ordinal).Skip(query.Offset).ToList();
        }
        var page = all.Take(query.Limit).ToList();
        var trashers = page.Select(x => x.TrashedById).Distinct().ToList();
        var names = trashers.Count == 0 ? new Dictionary<Guid, string>() : (await m_session.QueryOver<UserAccount>()
            .WhereRestrictionOn(x => x.Id).IsIn(trashers)
            .Select(x => x.Id, x => x.DisplayName)
            .ListAsync<object[]>(ct)).ToDictionary(x => (Guid)x[0], x => (string)x[1]);
        var items = page.Select(x => x with { TrashedByName = names.GetValueOrDefault(x.TrashedById) }).ToArray();
        return new PageTrashDto(spaceId, await TreeVersion.ReadAsync(m_session, spaceId, ct), items, all.Count > query.Limit);
    }
}
