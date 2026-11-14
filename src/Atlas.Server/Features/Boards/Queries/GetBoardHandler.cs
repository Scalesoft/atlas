using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Queries;

public sealed class GetBoardHandler
{
    private readonly ISession m_session;
    private readonly BoardAccess m_boards;
    private readonly BoardPageTargets m_targets;
    private readonly BoardObjectTargets m_objects;

    public GetBoardHandler(ISession session, BoardAccess boards, BoardPageTargets targets, BoardObjectTargets objects)
    {
        m_session = session;
        m_boards = boards;
        m_targets = targets;
        m_objects = objects;
    }

    /// <summary>The visible items at the board's sequence (the board row FOR SHARE keeps batches out meanwhile). A page or object
    /// card the reader may not open shows neither its title nor whether its target exists.</summary>
    public async Task<BoardDto> Handle(GetBoard query, CancellationToken ct)
    {
        var opened = await m_boards.OpenAsync(query.BoardId, false, null, ct);
        var board = opened.Board;
        var items = await m_session.QueryOver<BoardItem>()
            .Where(x => x.BoardId == board.Id && !x.IsDeleted)
            .OrderBy(x => x.Z).Asc.ThenBy(x => x.Id).Asc
            .ListAsync(ct);
        var visible = BoardEditing.Visible(items.Select(item => item.ToState()).ToArray());
        var objectCards = visible.Where(item => item.Kind == BoardItemKinds.Object).ToArray();
        var pageIds = visible.Except(objectCards).Select(item => item.TargetResourceId).OfType<Guid>().ToArray();
        var cards = await m_targets.ReadableAsync(opened, pageIds, ct);
        // A readable page in the trash says so (its card is no locked one) and whether this reader may restore it.
        var trashed = await m_targets.TrashedAsync(opened, pageIds.Where(id => !cards.ContainsKey(id)).ToArray(), ct);
        cards = trashed.Count == 0 ? cards : cards.Concat(trashed).ToDictionary(pair => pair.Key, pair => pair.Value);
        var objects = await m_objects.ReadableAsync(opened.Actor, objectCards.Select(item => item.TargetResourceId).OfType<Guid>().ToArray(), ct);
        return new BoardDto(board.Id, board.SpaceId, board.Title, board.Sequence, opened.CanEdit, BoardJson.Element(BoardProps.WithDefaults(board.SettingsJson)),
            visible.Select(item => BoardItemDto.Of(item, item.TargetResourceId is { } target ? Target(item, target, cards, objects) : null)).ToArray())
        {
            OwnerPageId = board.Purpose is null ? null : board.ParentPageId
        };
    }

    private static BoardTargetDto Target(BoardItemState item, Guid target, IReadOnlyDictionary<Guid, BoardTargetDto> cards, IReadOnlyDictionary<Guid, BoardTargetDto> objects)
    {
        return item.Kind == BoardItemKinds.Object
            ? objects.GetValueOrDefault(target) ?? BoardTargetDto.Unavailable(target, BoardProps.ObjectTarget(JsonNode.Parse(item.Props) as JsonObject)!.Value.TypeId)
            : cards.GetValueOrDefault(target) ?? BoardTargetDto.Unavailable(target);
    }
}
