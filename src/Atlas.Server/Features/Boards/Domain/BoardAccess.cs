using Atlas.Server.Features.Mcp;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Domain;

/// <summary>
/// Opens a board for the request account in the content lock order (architecture, Konvence kódu): the shared content lock, the
/// board's space and the spaces of the pages it shows FOR SHARE, the account, then the board row, FOR UPDATE for a batch and
/// FOR SHARE for a read (so a read's items and sequence are those of one committed batch). The space role decides: no role is
/// 404, as for pages; Reader reads; a write needs Writer (403, the board's existence is no secret to its reader); a board under a page with its own access (Page.OwnAccess)
/// needs that page's read, and its edit for a write, besides. A page's own board
/// (<see cref="Board.Purpose"/>) is decided by its page alone instead: who may edit the page edits it; who may only read the page reads
/// the <see cref="Board.PagePurpose"/> board and gets 404 for any other purpose (review S4-3); anyone else gets 404. On MCP (G5)
/// a board in a space switched out of AI is missing too, and its page cards into such spaces read as unavailable.
/// </summary>
public sealed class BoardAccess
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly BoardPageTargets m_targets;
    private readonly McpVisibility m_visibility;
    private readonly IRequestChannel m_channel;

    public BoardAccess(ISession session, IContentAccess access, BoardPageTargets targets, McpVisibility visibility, IRequestChannel channel)
    {
        m_session = session;
        m_access = access;
        m_targets = targets;
        m_visibility = visibility;
        m_channel = channel;
    }

    /// <param name="pageTargets">The pages to decide in this transaction; null for the page cards the board shows now.</param>
    public async Task<OpenedBoard> OpenAsync(Guid boardId, bool forEdit, IReadOnlyCollection<Guid>? pageTargets, CancellationToken ct)
    {
        // A board never changes its space or its purpose, so unlike a page's they may be read before the content lock. The page cards
        // read here are only the spaces to lock; what a card shows is decided under those locks, and a card added meanwhile reads as
        // unavailable.
        var head = await m_session.QueryOver<Board>().Where(x => x.Id == boardId).Select(x => x.SpaceId, x => x.Purpose).SingleOrDefaultAsync<object[]>(ct);
        if (head is null)
        {
            throw BoardErrors.NotFound();
        }
        var (spaceId, purpose) = ((Guid)head[0], head[1] as string);
        var ownedByPage = purpose is not null;
        pageTargets ??= (await m_session.QueryOver<BoardItem>()
            .Where(x => x.BoardId == boardId && !x.IsDeleted && x.Kind == BoardItemKinds.Page)
            .Select(x => x.TargetResourceId)
            .ListAsync<Guid?>(ct)).OfType<Guid>().Distinct().ToArray();
        var pageSpaces = await m_targets.LocateAsync(pageTargets, ct);
        var spaces = pageSpaces.Values.Append(spaceId).ToHashSet();
        var actor = await m_access.LockActorInSpacesAsync(spaces, ct);
        if (m_channel.Current.IsAiBound)
        {
            var excluded = await m_visibility.ExcludedSpacesAsync(spaces, ct);
            if (excluded.Contains(spaceId))
            {
                throw BoardErrors.NotFound();
            }
            // Left out of the cards' spaces, a page there reads as unavailable and cannot be placed (BoardPageTargets.ReadableAsync).
            pageSpaces = pageSpaces.Where(page => !excluded.Contains(page.Value)).ToDictionary();
            spaces.ExceptWith(excluded);
        }
        var canEdit = false;
        if (!ownedByPage)
        {
            try
            {
                canEdit = await m_access.RequireSpaceAsync(spaceId, SpaceRole.Reader, ct) >= SpaceRole.Writer;
            }
            catch (ApiException ex) when (ex.Status == 404)
            {
                throw BoardErrors.NotFound();
            }
        }
        var board = forEdit ? await m_session.GetForUpdateAsync<Board>(boardId, ct) : await ReadSharedAsync(boardId, ct);
        if (board is null || board.IsTrashed)
        {
            throw BoardErrors.NotFound();
        }
        if (ownedByPage)
        {
            // A page's own board is decided by the page alone, so a page grant is enough without a space role (same space, locked above).
            // It shows the page's working revision (review S4-3): only who may edit the page reaches it, except the page's board, which
            // the page's readers read as its editors composed it (owner, 3.5.1 §1.10; an unpublished page only its owner, as the page).
            var page = await m_session.GetAsync<Page>(board.ParentPageId!.Value, ct);
            var decision = page is null || page.IsTrashed ? null : await m_access.AuthorizePageAsync(page, AccessOperation.ReadContent, ct);
            if (decision is not { Allowed: true } || !decision.CanEdit && purpose != Board.PagePurpose)
            {
                throw BoardErrors.NotFound();
            }
            canEdit = decision.CanEdit;
        }
        else if (board.ParentPageId is { } parentId && await m_session.GetAsync<Page>(parentId, ct) is { OwnAccess: true } restricted)
        {
            // A space board under a page with its own access is that page's too (M1): only who reads the page reads it, only who
            // edits the page edits it; the space role is still needed, as for any space board.
            var decision = await m_access.AuthorizePageAsync(restricted, AccessOperation.ReadContent, ct);
            if (!decision.Allowed)
            {
                throw BoardErrors.NotFound();
            }
            canEdit = canEdit && decision.CanEdit;
        }
        if (forEdit && !canEdit)
        {
            throw BoardErrors.ReadOnly();
        }
        return new OpenedBoard(board, actor, canEdit, pageSpaces, spaces);
    }

    // Native SQL, listed in RowLockTextConventionTests: NHibernate has no FOR SHARE lock mode, and IAtlasLocks (the platform)
    // does not know the tables of an optional module. Nothing in this transaction has loaded the board yet.
    private Task<Board?> ReadSharedAsync(Guid boardId, CancellationToken ct)
    {
        return m_session.CreateSQLQuery("SELECT {b.*} FROM \"Board\" {b} WHERE {b}.\"Id\"=:id FOR SHARE")
            .AddEntity("b", typeof(Board))
            .SetParameter("id", boardId)
            .UniqueResultAsync<Board?>(ct);
    }
}
