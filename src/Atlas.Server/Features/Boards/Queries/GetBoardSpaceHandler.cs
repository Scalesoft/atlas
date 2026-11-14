using Atlas.Server.Features.Boards.Domain;

namespace Atlas.Server.Features.Boards.Queries;

public sealed class GetBoardSpaceHandler
{
    private readonly BoardAccess m_boards;

    public GetBoardSpaceHandler(BoardAccess boards)
    {
        m_boards = boards;
    }

    public async Task<BoardSpace> Handle(GetBoardSpace query, CancellationToken ct)
    {
        return new BoardSpace((await m_boards.OpenAsync(query.BoardId, false, [], ct)).Board.SpaceId);
    }
}
