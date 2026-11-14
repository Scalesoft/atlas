using System.Text.Json.Nodes;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Commands;

/// <summary>
/// The page's trash on the object path (IObjectTrash; POST /api/objects/{id}/restore and /purge), with the rights of the space trash:
/// ManageAccess, which a space Admin holds on every page of the space (D4). The permanent delete also needs the space Admin role (or
/// WikiAdmin), as a board's: a page's own manage grant reads no space trash and restores nothing, so it deletes nothing for good either
/// (SEC3-2). The restore puts the page back under its parent
/// (UntrashPage); the space trash's own restore chooses another. The permanent delete (unified trash spec §2, §3) takes the page with
/// the subpages trashed with it in its batch and the tree's nodes under them (a board, a link); a subpage trashed earlier in a batch
/// of its own stays in the trash and comes back under another parent, as one whose parent is in the trash. One audit row (page.purged)
/// with the number of pages and of the objects that went with them (boards). The web's only: MCP has no route to it (rule 6).
/// </summary>
public sealed class PageObjectTrash : IObjectTrash
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly OrganizationLocks m_locks;
    private readonly IAtlasLocks m_atlasLocks;
    private readonly ContentParticipants m_participants;
    private readonly AccessChanges m_changes;
    private readonly AttachmentFiles m_files;
    private readonly UntrashPageHandler m_untrash;
    private readonly ILogger<PageObjectTrash> m_log;
    private readonly IRequestChannel m_channel;
    private readonly Mcp.McpVisibility m_visibility;

    public PageObjectTrash(ISession session, IContentAccess access, OrganizationLocks locks, IAtlasLocks atlasLocks, ContentParticipants participants,
        AccessChanges changes, AttachmentFiles files, UntrashPageHandler untrash, ILogger<PageObjectTrash> log, IRequestChannel channel,
        Mcp.McpVisibility visibility)
    {
        m_channel = channel;
        m_visibility = visibility;
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_atlasLocks = atlasLocks;
        m_participants = participants;
        m_changes = changes;
        m_files = files;
        m_untrash = untrash;
        m_log = log;
    }

    public string TypeId => Pages.PagesAtlasModule.ContentType;

    public async Task<long> RestoreAsync(Guid objectId, CancellationToken ct)
    {
        var place = await m_session.QueryOver<Page>().Where(x => x.Id == objectId).Select(x => x.SpaceId, x => x.ParentId).SingleOrDefaultAsync<object[]>(ct)
            ?? throw OrganizationLocks.Missing();
        if (m_channel.Current.IsAiBound)
        {
            // G5: a space switched out of AI is missing to MCP (atlas_restore_object).
            await m_visibility.RequireSpaceAsync((Guid)place[0], ct);
        }
        var version = await TreeVersion.ReadAsync(m_session, (Guid)place[0], ct);
        await m_untrash.Handle(new UntrashPage(objectId, new UntrashPageRequest((Guid?)place[1], false, version)), ct);
        return await m_session.QueryOver<Page>().Where(x => x.Id == objectId).Select(x => x.Version).SingleOrDefaultAsync<int>(ct);
    }

    public async Task PurgeAsync(Guid objectId, CancellationToken ct)
    {
        var (page, actor) = await m_locks.LockPageAsync(objectId, true, AccessOperation.ManageAccess, ct);
        await OrganizationLocks.RequireSpaceAdministrationAsync(m_access, page.SpaceId, "Stránku z koše trvale maže správce prostoru.", ct);
        var ids = await PageSubtree.DescendantsAsync(m_session, page, true, ct);
        var pages = await PageSubtree.LockSelectionAsync(m_session, m_access, m_atlasLocks, ids, AccessOperation.ManageAccess, ct);
        var pageId = page.Id;
        var title = await m_session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null).Select(x => x.Title).SingleOrDefaultAsync<string>(ct);
        var detail = new JsonObject { ["pages"] = ids.Count };
        await ObjectPurge.ParticipantsAsync(m_participants, page.SpaceId, ids, page.TrashBatchId, actor.Id, detail, ct);
        var files = await PagePurge.RunAsync(m_session, pages, actor.Id, ct);
        await ObjectPurge.AuditAsync(m_session, page.Id, TypeId, ObjectScopes.Space, page.SpaceId, title, actor.Id, detail, ct);
        m_changes.Changed();
        m_changes.AfterCommit(() =>
        {
            PagePurge.RemoveFiles(m_files, files, m_log);
            return Task.CompletedTask;
        });
    }
}
