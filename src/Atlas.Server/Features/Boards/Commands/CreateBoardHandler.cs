using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Commands;

public sealed class CreateBoardHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly OrganizationLocks m_locks;
    private readonly ContentParticipants m_participants;
    private readonly McpVisibility m_visibility;
    private readonly IRequestChannel m_channel;

    public CreateBoardHandler(ISession session, IContentAccess access, OrganizationLocks locks, ContentParticipants participants, McpVisibility visibility,
        IRequestChannel channel)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_participants = participants;
        m_visibility = visibility;
        m_channel = channel;
    }

    /// <summary>An empty board at sequence 0, the last of its siblings in the space tree (pages included), placed under the
    /// organization's locks (the space FOR UPDATE) as a page's move is; its ContentResource row is written with it (ContentRowEffects). On MCP the
    /// space must take part in AI (G5). The page's board (a Purpose) is decided by its page alone, as BoardAccess opens it: who may edit
    /// the page makes it, once per page (the space lock keeps a second one out).</summary>
    public async Task<BoardDto> Handle(CreateBoard command, CancellationToken ct)
    {
        var title = Rules.Required(command.Title, "název", BoardEditing.MaxTitle);
        var actor = await m_locks.LockForWriteAsync(command.SpaceId, ct);
        var role = command.Purpose is null ? await m_access.RequireSpaceAsync(command.SpaceId, SpaceRole.Reader, ct) : SpaceRole.Writer;
        if (m_channel.Current.IsAiBound)
        {
            await m_visibility.RequireSpaceAsync(command.SpaceId, ct);
        }
        if (role < SpaceRole.Writer)
        {
            throw BoardErrors.ReadOnly();
        }
        if (command.Purpose is not null)
        {
            ExpectPagePurpose(command);
        }
        await PageParent.ValidateNodeAsync(m_session, m_access, command.SpaceId, command.ParentPageId, ct);
        // Only who edits the page learns it has its board.
        if (command.Purpose is not null && await m_session.QueryOver<Board>()
                .Where(x => x.ParentPageId == command.ParentPageId && x.Purpose == command.Purpose && !x.IsTrashed).RowCountAsync(ct) > 0)
        {
            throw new ApiException(409, "Stránka už svou tabuli má.", "board_exists");
        }
        var now = DateTime.UtcNow;
        var board = new Board
        {
            Id = Guid.NewGuid(), SpaceId = command.SpaceId, ParentPageId = command.ParentPageId, Title = title, CreatedAt = now, CreatedBy = actor.Id,
            UpdatedAt = now, Purpose = command.Purpose
        };
        board.SortOrder = await PageSiblings.PositionAsync(m_session, m_participants.Tree, board.SpaceId, board.Id, board.ParentPageId, null, actor, ct);
        await m_session.SaveAsync(board, ct);
        await BoardSearch.RefreshAsync(m_session, board.Id, ct);
        return new BoardDto(board.Id, board.SpaceId, board.Title, board.Sequence, true, BoardJson.Element(BoardProps.WithDefaults(board.SettingsJson)), []);
    }

    /// <summary>Only the page's board may be made with a purpose (the 3.3 projector.deployment is gone, a restore drops it), and under its
    /// page; that the actor edits the page ValidateNodeAsync decides.</summary>
    private static void ExpectPagePurpose(CreateBoard command)
    {
        if (command.Purpose != Board.PagePurpose)
        {
            throw new ApiException(400, "Tabule může mít jen účel {pagePurpose}.", "invalid_purpose", args: new Dictionary<string, object?> { ["pagePurpose"] = Board.PagePurpose });
        }
        if (command.ParentPageId is null)
        {
            throw new ApiException(400, "Tabule stránky potřebuje svou stránku (parentPageId).", "invalid_parent");
        }
    }
}
