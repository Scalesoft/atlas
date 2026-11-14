using Atlas.Server.Platform.Objects;

namespace Atlas.Server.Features.Pages.Objects;

/// <summary>The page view <c>page:{pageId}</c>: the page itself. Its embedded objects (objectRef) join with package E.</summary>
public sealed class PageObjectView : IObjectView
{
    public string Kind => "page";

    public string TypeId => PagesAtlasModule.ContentType;

    public Task<IReadOnlyCollection<Guid>> MembersAsync(Guid viewId, CancellationToken ct) => Task.FromResult<IReadOnlyCollection<Guid>>([]);
}
