using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Platform.Objects;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards;

/// <summary>The board view <c>board:{boardId}</c>: the board and every object its live items show (a page card's page).</summary>
public sealed class BoardObjectView : IObjectView
{
    private readonly ISession m_session;

    public BoardObjectView(ISession session)
    {
        m_session = session;
    }

    public string Kind => BoardLiveChannel.Prefix;

    public string TypeId => BoardsAtlasModule.ContentType;

    public async Task<IReadOnlyCollection<Guid>> MembersAsync(Guid viewId, CancellationToken ct)
    {
        var targets = await m_session.QueryOver<BoardItem>()
            .Where(x => x.BoardId == viewId && !x.IsDeleted && x.TargetResourceId != null)
            .Select(x => x.TargetResourceId)
            .ListAsync<Guid?>(ct);
        return targets.OfType<Guid>().Distinct().ToArray();
    }
}
