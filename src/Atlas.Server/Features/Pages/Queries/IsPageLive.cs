using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Queries;

/// <summary>3.6-I Živě: whether the page's readers read its latest saved state (a published page whose effective mode is Live). For the
/// server's own relays, not an access decision: who receives what is decided by their subscriptions.</summary>
public sealed record IsPageLive(Guid PageId);

public static class IsPageLiveHandler
{
    public static async Task<bool> Handle(IsPageLive query, ISession session, CancellationToken ct)
        => await session.GetAsync<Page>(query.PageId, ct) is { PublishedRevisionId: not null } page
           && await PublicationModes.EffectiveAsync(session, page, ct) == PublicationMode.Live;
}
