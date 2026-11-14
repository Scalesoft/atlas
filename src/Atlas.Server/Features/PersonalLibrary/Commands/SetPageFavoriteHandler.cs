using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalLibrary.Commands;

public sealed class SetPageFavoriteHandler
{
    private readonly ISession m_session;
    private readonly PersonalPageGuard m_guard;

    public SetPageFavoriteHandler(ISession session, PersonalPageGuard guard)
    {
        m_session = session;
        m_guard = guard;
    }

    public async Task<PersonalPageState> Handle(SetPageFavorite command, CancellationToken ct)
    {
        var pageId = command.PageId;
        var actor = await m_guard.RequirePageAsync(pageId, true, ct);
        var actorId = actor.Id;
        var current = await m_session.QueryOver<PageFavorite>()
            .Where(x => x.UserId == actorId && x.PageId == pageId && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        if (command.Request.IsFavorite && current is null)
        {
            var previous = await m_session.QueryOver<PageFavorite>()
                .Where(x => x.UserId == actorId && x.PageId == pageId)
                .OrderBy(x => x.ActiveFrom).Desc
                .Take(1)
                .SingleOrDefaultAsync(ct);
            var now = previous is null ? DateTime.UtcNow : Temporal.NextBoundary(previous.ActiveTo ?? previous.ActiveFrom, DateTime.UtcNow);
            await m_session.SaveAsync(new PageFavorite { Id = Guid.NewGuid(), UserId = actorId, PageId = pageId, ActiveFrom = now }, ct);
        }
        else if (!command.Request.IsFavorite && current is not null)
        {
            current.ActiveTo = Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
        }
        await m_session.FlushAsync(ct);
        return await m_guard.ReadStateAsync(pageId, actorId, ct);
    }
}
