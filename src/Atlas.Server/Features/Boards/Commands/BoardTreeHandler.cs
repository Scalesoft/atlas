using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Commands;

/// <summary>
/// A board's place in the space tree, with the discipline of the page organization (PageOrganization): the shared content lock,
/// the space FOR UPDATE, the account, then the board row FOR UPDATE; the tree's expected version; the parent's rules
/// (PageParent.ValidateNodeAsync) and the one sibling order of pages and boards (PageSiblings). Moving needs Writer (a reader
/// knows the board, so 403); restoring from the trash and the permanent delete need the space's Admin, as for pages. On MCP the space must
/// take part in AI (G5). It is also the board's trash on the object path (IObjectTrash), so one route restores and purges every type.
/// </summary>
public sealed class BoardTreeHandler : IObjectTrash
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly OrganizationLocks m_locks;
    private readonly ContentParticipants m_participants;
    private readonly McpVisibility m_visibility;
    private readonly IRequestChannel m_channel;
    private readonly AccessChanges m_changes;

    public BoardTreeHandler(ISession session, IContentAccess access, OrganizationLocks locks, ContentParticipants participants, McpVisibility visibility,
        IRequestChannel channel, AccessChanges changes)
    {
        m_changes = changes;
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_participants = participants;
        m_visibility = visibility;
        m_channel = channel;
    }

    public string TypeId => BoardsAtlasModule.ContentType;

    public async Task<OrganizationChange> Handle(MoveBoard command, CancellationToken ct)
    {
        var request = command.Request;
        var (board, actor) = await LockAsync(command.BoardId, false, SpaceRole.Writer, ct);
        if (board.Purpose is not null)
        {
            throw new ApiException(400, "Tabule patří ke své stránce a přesouvá se s ní.", "invalid_parent");
        }
        await PageParent.RequireCurrentAsync(m_session, m_access, board.ParentPageId, BoardErrors.NotFound(), ct);
        await TreeVersion.ExpectAsync(m_session, board.SpaceId, request.ExpectedTreeVersion, ct);
        await PageParent.ValidateNodeAsync(m_session, m_access, board.SpaceId, request.ParentId, ct);
        var cancelled = await TitleOrder.HandMoveAsync(m_session, m_participants.Tree, actor, board.SpaceId, board.ParentPageId, request.ParentId, ct);
        board.SortOrder = await PageSiblings.PositionAsync(m_session, m_participants.Tree, board.SpaceId, board.Id, request.ParentId, request.BeforePageId, actor, ct);
        board.ParentPageId = request.ParentId;
        board.UpdatedAt = DateTime.UtcNow;
        await m_session.FlushAsync(ct);
        return new OrganizationChange(await TreeVersion.ReadAsync(m_session, board.SpaceId, ct), [board.Id], TitleOrderCancelled: cancelled);
    }

    public Task<OrganizationChange> Handle(UntrashBoard command, CancellationToken ct)
        => UntrashAsync(command.BoardId, command.Request.ParentId, command.Request.ExpectedTreeVersion, ct);

    /// <summary>The object path's restore (ObjectTrashHandler): back under the page it was under, or at the root.</summary>
    async Task<long> IObjectTrash.RestoreAsync(Guid objectId, CancellationToken ct)
    {
        var parentId = await m_session.QueryOver<Board>().Where(x => x.Id == objectId).Select(x => x.ParentPageId).SingleOrDefaultAsync<Guid?>(ct);
        await UntrashAsync(objectId, parentId, null, ct);
        return await m_session.QueryOver<ContentResource>().Where(x => x.Id == objectId).Select(x => x.Version).SingleOrDefaultAsync<long>(ct);
    }

    /// <summary>The permanent delete of a board of a space from its trash (unified trash spec §2, §3): the space's Admin, as its restore.
    /// Its items, log and search document go (BoardPurge) and one audit row stays.</summary>
    async Task IObjectTrash.PurgeAsync(Guid objectId, CancellationToken ct)
    {
        var (board, actor) = await LockAsync(objectId, true, SpaceRole.Admin, ct);
        await ObjectPurge.AuditAsync(m_session, board.Id, TypeId, ObjectScopes.Space, board.SpaceId, board.Title, actor.Id, null, ct);
        m_session.Evict(board);
        await BoardPurge.DeleteAsync(m_session, [board.Id], actor.Id, ct);
        m_changes.Changed();
    }

    /// <param name="expectedTreeVersion">Null: the object path's restore to its place, under the space lock without the tree's version.</param>
    private async Task<OrganizationChange> UntrashAsync(Guid boardId, Guid? parentId, long? expectedTreeVersion, CancellationToken ct)
    {
        var (board, actor) = await LockAsync(boardId, true, SpaceRole.Admin, ct);
        if (expectedTreeVersion is { } expected)
        {
            await TreeVersion.ExpectAsync(m_session, board.SpaceId, expected, ct);
        }
        if (board.Purpose is not null)
        {
            await ExpectOwnPageAsync(board, parentId, ct);
        }
        else
        {
            await PageParent.ValidateNodeAsync(m_session, m_access, board.SpaceId, parentId, ct);
            board.SortOrder = await PageSiblings.PositionAsync(m_session, m_participants.Tree, board.SpaceId, board.Id, parentId, null, actor, ct);
            board.ParentPageId = parentId;
        }
        board.IsTrashed = false;
        board.TrashedAt = null;
        board.TrashedById = null;
        board.TrashBatchId = null;
        board.UpdatedAt = DateTime.UtcNow;
        await m_session.FlushAsync(ct);
        return new OrganizationChange(await TreeVersion.ReadAsync(m_session, board.SpaceId, ct), [board.Id]);
    }

    /// <summary>A page's own board comes back only under its page (review V5): the page out of the trash and editable by the actor (as the
    /// board is, BoardAccess), and no newer board of the same purpose there (one per page). The space is held FOR UPDATE, so no opening
    /// of the page creates one meanwhile.</summary>
    private async Task ExpectOwnPageAsync(Board board, Guid? parentId, CancellationToken ct)
    {
        if (parentId is { } parent && parent != board.ParentPageId)
        {
            throw new ApiException(409, "Tabule patří ke své stránce; obnovit ji jde jen pod ni.", "invalid_parent");
        }
        var page = await m_session.GetAsync<Page>(board.ParentPageId!.Value, ct);
        if (page is null || page.IsTrashed)
        {
            throw new ApiException(409, "Stránka tabule je v koši; obnovte nejdřív ji.", "page_trashed");
        }
        var decision = await m_access.AuthorizePageAsync(page, AccessOperation.EditContent, ct);
        if (decision.Outcome == PageAccessOutcome.Missing)
        {
            throw BoardErrors.NotFound();
        }
        if (decision.Outcome == PageAccessOutcome.Forbidden)
        {
            throw new ApiException(403, "Tabuli stránky obnovuje jen ten, kdo smí stránku upravovat.");
        }
        var newer = await m_session.QueryOver<Board>()
            .Where(x => x.ParentPageId == board.ParentPageId && x.Purpose == board.Purpose && !x.IsTrashed && x.Id != board.Id)
            .RowCountAsync(ct);
        if (newer > 0)
        {
            throw new ApiException(409, "Stránka už má novější tabuli; starou z koše obnovit nejde.", "board_exists");
        }
    }

    private async Task<(Board Board, UserAccount Actor)> LockAsync(Guid boardId, bool trashed, SpaceRole required, CancellationToken ct)
    {
        // Lock order: the shared advisory before anything reads content; a board never changes space outside a page's move, which
        // holds the space FOR UPDATE too, so its space read first stays its space under the lock (checked below).
        var spaceId = await m_session.QueryOver<Board>().Where(x => x.Id == boardId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        if (spaceId == Guid.Empty)
        {
            throw BoardErrors.NotFound();
        }
        var actor = await m_locks.LockForWriteAsync(spaceId, ct);
        SpaceRole role;
        try
        {
            role = await m_access.RequireSpaceAsync(spaceId, SpaceRole.Reader, ct);
            if (m_channel.Current.IsAiBound)
            {
                await m_visibility.RequireSpaceAsync(spaceId, ct);
            }
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
            throw BoardErrors.NotFound();
        }
        var board = await m_session.GetForUpdateAsync<Board>(boardId, ct);
        if (board is null || board.SpaceId != spaceId || board.IsTrashed != trashed)
        {
            throw BoardErrors.NotFound();
        }
        if (role < required)
        {
            throw required == SpaceRole.Writer ? BoardErrors.ReadOnly() : new ApiException(403, "Tabuli z koše obnovuje a trvale maže správce prostoru.");
        }
        return (board, actor);
    }
}
