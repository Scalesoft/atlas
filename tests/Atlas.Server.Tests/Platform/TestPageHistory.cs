using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageHistory.Queries;

namespace Atlas.Server.Tests.Platform;

/// <summary>Page history messages with an explicit actor: fixtures of not-yet-converted features assert through it.</summary>
internal sealed class TestPageHistory
{
    private readonly AtlasTestHost m_host;

    public TestPageHistory(AtlasTestHost host)
    {
        m_host = host;
    }

    public Task<PageHistoryList> ListAsync(Guid id, UserAccount actor, int? beforeVersion = null, int limit = 20, Guid? group = null, CancellationToken ct = default)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<PageHistoryList>(new GetPageHistory(id, beforeVersion, limit, group), ct);
    }

    public Task<PageHistoryRevision> GetRevisionAsync(Guid id, Guid revisionId, UserAccount actor, CancellationToken ct = default)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<PageHistoryRevision>(new GetPageRevision(id, revisionId), ct);
    }

    public Task<PageDto> RestoreAsync(Guid id, RestorePageRequest request, UserAccount actor, CancellationToken ct = default)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<PageDto>(new RestorePage(id, request), ct);
    }
}
