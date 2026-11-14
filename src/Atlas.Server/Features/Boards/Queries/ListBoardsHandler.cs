using Atlas.Server.Features.Boards.Contracts;
using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Boards.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Queries;

public sealed class ListBoardsHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public ListBoardsHandler(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    /// <summary>The space's boards outside the trash, by title, for a Reader of the space. A page's own board (Purpose) is no board of
    /// the space: its page decides who reaches it, and it is reached from there (review S4-1). A board under a page with its own access only
    /// for who reads that page (M1).</summary>
    public async Task<BoardList> Handle(ListBoards query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(query.SpaceId, ct);
        await m_access.RequireSpaceAsync(query.SpaceId, SpaceRole.Reader, ct);
        var boards = await m_session.QueryOver<Board>()
            .Where(x => x.SpaceId == query.SpaceId && !x.IsTrashed && x.Purpose == null)
            .And(Expression.Sql(BoardContent.UnderReadablePage("{alias}", "?"), actor.Id, NHibernateUtil.Guid))
            .OrderBy(x => x.Title).Asc.ThenBy(x => x.Id).Asc
            .ListAsync(ct);
        if (boards.Count == 0)
        {
            return new BoardList([]);
        }
        // What a reader of each board sees (BoardEditing.Visible): live items without a connector whose end is deleted. The ends
        // always exist (soft delete, foreign keys).
        var counts = (await m_session.CreateSQLQuery("""
                SELECT i."BoardId", count(*) FROM "BoardItem" i
                WHERE i."BoardId" IN (:boards) AND NOT i."IsDeleted" AND (i."Kind"<>:connector OR NOT EXISTS (
                    SELECT 1 FROM "BoardItem" e WHERE e."Id" IN (i."FromItemId", i."ToItemId") AND e."IsDeleted"))
                GROUP BY i."BoardId"
                """)
            .SetParameterList("boards", boards.Select(board => board.Id).ToArray())
            .SetParameter("connector", BoardItemKinds.Connector)
            .ListAsync<object[]>(ct))
            .ToDictionary(row => (Guid)row[0], row => Convert.ToInt32(row[1]));
        return new BoardList(boards.Select(board => new BoardSummaryDto(board.Id, board.Title, board.UpdatedAt, counts.GetValueOrDefault(board.Id))).ToArray());
    }
}
