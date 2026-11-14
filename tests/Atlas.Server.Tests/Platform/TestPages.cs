using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;

namespace Atlas.Server.Tests.Platform;

/// <summary>Page messages with an explicit actor: fixtures of not-yet-converted features seed and assert through it.</summary>
internal sealed class TestPages
{
    private readonly AtlasTestHost m_host;

    public TestPages(AtlasTestHost host)
    {
        m_host = host;
    }

    public Task<PageDto> GetAsync(Guid id, UserAccount actor, CancellationToken ct)
    {
        return AsAsync(actor, new GetPage(id), ct);
    }

    public Task<PageDto> CreateAsync(CreatePageRequest request, UserAccount actor, CancellationToken ct)
    {
        return AsAsync(actor, new CreatePage(request), ct);
    }

    public Task<PageDto> SaveAsync(Guid id, SavePageRequest request, UserAccount actor, CancellationToken ct)
    {
        return AsAsync(actor, new SavePage(id, request), ct);
    }

    public Task<PageDto> PublishAsync(Guid id, PublishPageRequest request, UserAccount actor, CancellationToken ct)
    {
        return AsAsync(actor, new PublishPage(id, request), ct);
    }

    private Task<PageDto> AsAsync(UserAccount actor, object message, CancellationToken ct)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<PageDto>(message, ct);
    }
}
