using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Live;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Commands;

public sealed class DeleteBoardHandler
{
    private readonly BoardAccess m_boards;
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly AccessChanges m_changes;
    private readonly LiveRelay m_relay;

    public DeleteBoardHandler(BoardAccess boards, ISession session, IContentAccess access, AccessChanges changes, LiveRelay relay)
    {
        m_session = session;
        m_access = access;
        m_boards = boards;
        m_changes = changes;
        m_relay = relay;
    }

    /// <summary>To the space's trash with a batch of its own (UntrashBoard brings it back, a page's restore does not); its
    /// subscribers are revalidated after the commit, so they get Revoked at once.</summary>
    // shortcut: no permanent delete, as pages have none yet; it comes with the trash's purge for both.
    public async Task Handle(DeleteBoard command, CancellationToken ct)
    {
        var opened = await m_boards.OpenAsync(command.BoardId, true, [], ct);
        var board = opened.Board;
        if (board.Purpose is null)
        {
            // The trash takes the board from under its page as a move does (SEC-Z-3); a page's own board is decided by its page in OpenAsync.
            await PageParent.RequireCurrentAsync(m_session, m_access, board.ParentPageId, BoardErrors.NotFound(), ct);
        }
        var now = DateTime.UtcNow;
        board.IsTrashed = true;
        board.TrashedAt = now;
        board.TrashedById = opened.Actor.Id;
        board.TrashBatchId = Guid.NewGuid();
        board.UpdatedAt = now;
        m_changes.AfterCommit(() => m_relay.RefreshAsync(BoardLiveChannel.Name(board.Id), CancellationToken.None));
    }
}
