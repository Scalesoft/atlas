using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Platform.Validation;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Queries;

public sealed class GetRemovedBoardItemsHandler
{
    public const int Limit = 50;
    private const int TextLength = 120;

    private readonly ISession m_session;
    private readonly BoardAccess m_boards;
    private readonly BoardPageTargets m_targets;

    public GetRemovedBoardItemsHandler(ISession session, BoardAccess boards, BoardPageTargets targets)
    {
        m_session = session;
        m_boards = boards;
        m_targets = targets;
    }

    /// <summary>The deleted items, the most recently changed first (a deletion is the last change of the item); connectors are left
    /// out (they come back with their ends). Only a Writer restores, so only a Writer reads the list.</summary>
    public async Task<RemovedBoardItemsDto> Handle(GetRemovedBoardItems query, CancellationToken ct)
    {
        var removed = await m_session.QueryOver<BoardItem>()
            .Where(x => x.BoardId == query.BoardId && x.IsDeleted && x.Kind != BoardItemKinds.Connector)
            .OrderBy(x => x.Version).Desc
            .Take(Limit)
            .ListAsync(ct);
        var pageIds = removed.Where(item => item.Kind == BoardItemKinds.Page).Select(item => item.TargetResourceId).OfType<Guid>().Distinct().ToArray();
        var opened = await m_boards.OpenAsync(query.BoardId, false, pageIds, ct);
        if (!opened.CanEdit)
        {
            throw new ApiException(403, "Odebrané prvky vidí jen ten, kdo smí tabuli upravovat.");
        }
        var cards = await m_targets.ReadableAsync(opened, pageIds, ct);
        return new RemovedBoardItemsDto(removed.Select(item => new RemovedBoardItemDto(item.Id, item.Kind,
            item.TargetResourceId is { } target && cards.TryGetValue(target, out var card) ? card.Title : Cut(item.Text))).ToArray());
    }

    private static string? Cut(string? text)
    {
        return text is { Length: > TextLength } ? text[..TextLength] + "…" : text;
    }
}
