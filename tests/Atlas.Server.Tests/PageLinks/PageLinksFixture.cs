using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.PageLinks;

/// <summary>Page links over the real message bus: a WikiAdmin, two writers and a reader of one space, and a space the reader
/// and the writers do not reach.</summary>
internal sealed class PageLinksFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;
    private readonly HashSet<Guid> m_spaces = [];

    private PageLinksFixture(AtlasTestHost host)
    {
        m_host = host;
        Pages = new TestPages(host);
    }

    public AtlasTestHost Host => m_host;
    public TestPages Pages { get; }
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount Writer { get; private set; } = null!;
    public UserAccount OtherWriter { get; private set; } = null!;
    public UserAccount Reader { get; private set; } = null!;
    public Guid SpaceId { get; private set; }
    public Guid ForeignSpaceId { get; private set; }

    public static async Task<PageLinksFixture> CreateAsync()
    {
        var host = await AtlasTestHost.CreateAsync("atlas_page_links_");
        var f = new PageLinksFixture(host);
        f.Admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        f.Writer = await host.SeedUserAsync("writer");
        f.OtherWriter = await host.SeedUserAsync("other");
        f.Reader = await host.SeedUserAsync("reader");
        f.SpaceId = await host.SeedSpaceAsync("Handbook", (f.Writer, SpaceRole.Writer), (f.OtherWriter, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
        f.ForeignSpaceId = await host.SeedSpaceAsync("Foreign");
        return f;
    }

    /// <summary>A rich document with one paragraph of the given inline nodes.</summary>
    public static string Doc(params string[] inline) =>
        "{\"format\":\"atlas-rich\",\"version\":1,\"plugins\":[{\"id\":\"pages\",\"version\":1}],\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":["
        + string.Join(",", inline) + "]}]}}";

    public static string Ref(Guid id) =>
        $$$"""{"type":"pageReference","attrs":{"id":"{{{id}}}","title":"Odkaz","url":"#/pages/{{{id}}}"}}""";

    public static string Link(string href) =>
        $$$"""{"type":"text","text":"odkaz","marks":[{"type":"link","attrs":{"href":"{{{href}}}"}}]}""";

    public static string Text(string text) => $$$"""{"type":"text","text":"{{{text}}}"}""";

    public Task<PageDto> Create(string title, string? rich = null, Guid? spaceId = null, UserAccount? owner = null)
    {
        m_spaces.Add(spaceId ?? SpaceId);
        return Pages.CreateAsync(new CreatePageRequest(spaceId ?? SpaceId, null, title, rich ?? Doc(Text(title)), title), owner ?? Writer, default);
    }

    public async Task<PageDto> CreatePublished(string title, string? rich = null, Guid? spaceId = null, UserAccount? owner = null)
    {
        var page = await Create(title, rich, spaceId, owner);
        return await Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), owner ?? Writer, default);
    }

    public Task<PageDto> Save(PageDto page, string title, string rich, UserAccount? actor = null) =>
        Pages.SaveAsync(page.Id, new SavePageRequest(title, rich, title, page.Version), actor ?? Writer, default);

    public Task<PageDto> Publish(PageDto page, UserAccount? actor = null) =>
        Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), actor ?? Writer, default);

    public async Task<PageDto> Approve(PageDto page)
    {
        var revisionId = await WorkingRevision(page.Id);
        return await As<PageDto>(Admin, new PublishPage(page.Id, new PublishPageRequest(page.Version), new SpaceDraftReview(page.SpaceId, revisionId)));
    }

    public async Task Trash(PageDto page, UserAccount? actor = null)
    {
        actor ??= Writer;
        var tree = await As<PageTreeDto>(actor, new GetPageTree(page.SpaceId));
        await As<OrganizationChange>(actor, new TrashPage(page.Id, new TrashPageRequest(false, tree.Version, page.Version)));
    }

    /// <summary>The projections are derived after a commit by the outbox's deliveries, which this host has no queue for: they are
    /// derived from the pages written so far before each message and read (only in the spaces this fixture wrote pages in: the host is shared).</summary>
    public async Task<T> As<T>(UserAccount actor, object message)
    {
        await RefreshAsync();
        m_host.Actor.Current = actor;
        return await m_host.InvokeAsync<T>(message);
    }

    private Task RefreshAsync() => m_spaces.Count == 0 ? Task.CompletedTask : m_host.RefreshPageDerivedAsync([.. m_spaces]);

    public Task Execute(string sql, Guid id) => m_host.ExecuteAsync(sql, id);

    /// <summary>The projected targets of a source page, read past every handler.</summary>
    public async Task<Guid[]> Targets(Guid sourceId)
    {
        await RefreshAsync();
        using var session = m_host.OpenSession();
        var rows = await session.CreateSQLQuery("SELECT \"TargetPageId\" FROM \"PageLink\" WHERE \"SourcePageId\"=:id ORDER BY \"TargetPageId\"")
            .SetParameter("id", sourceId).ListAsync<Guid>();
        return rows.ToArray();
    }

    public async Task<Guid> WorkingRevision(Guid pageId)
    {
        using var session = m_host.OpenSession();
        return await session.CreateSQLQuery("SELECT \"Id\" FROM \"PageRevision\" WHERE \"PageId\"=:id AND \"ActiveTo\" IS NULL")
            .SetParameter("id", pageId).UniqueResultAsync<Guid>();
    }

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
