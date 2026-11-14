using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Relations;

/// <summary>
/// Every module on (pages, boards, contacts) and a space <see cref="SpaceId"/> where <see cref="Manager"/> is Admin, the boards
/// fixture's Writer writes and its Reader reads; its WikiAdmin and Outsider as there. Everything goes through the real messages.
/// </summary>
internal sealed class RelationsFixture : IAsyncDisposable
{
    private readonly bool m_outbox;

    private RelationsFixture(BoardsFixture boards, bool outbox)
    {
        m_outbox = outbox;
        Boards = boards;
        Pages = new TestPages(boards.Host);
    }

    public BoardsFixture Boards { get; }
    public TestPages Pages { get; }
    public AtlasTestHost Host => Boards.Host;
    public UserAccount Admin => Boards.Admin;
    public UserAccount Manager { get; private set; } = null!;
    public UserAccount Writer => Boards.Writer;
    public UserAccount Reader => Boards.Reader;
    public UserAccount Outsider => Boards.Outsider;
    public Guid SpaceId { get; private set; }

    /// <summary>The fixture on the boards host every test of the process shares (BoardsFixture.SharedAsync).</summary>
    public static Task<RelationsFixture> SharedAsync() => CreateAsync(BoardsFixture.SharedAsync(), false);

    /// <summary>A host of the test's own.</summary>
    /// <param name="outbox">A host whose outbox delivers (BoardsFixture).</param>
    public static Task<RelationsFixture> CreateAsync(bool outbox = false) => CreateAsync(BoardsFixture.CreateAsync(outbox: outbox), outbox);

    private static async Task<RelationsFixture> CreateAsync(Task<BoardsFixture> boards, bool outbox)
    {
        var fixture = new RelationsFixture(await boards, outbox);
        fixture.Manager = await fixture.Host.SeedUserAsync("manager");
        fixture.SpaceId = await fixture.Host.SeedSpaceAsync("Obsah", (fixture.Manager, SpaceRole.Admin), (fixture.Writer, SpaceRole.Writer),
            (fixture.Reader, SpaceRole.Reader));
        return fixture;
    }

    public static string PageRef(Guid id) => $$$"""{"type":"pageReference","attrs":{"id":"{{{id}}}","title":"Odkaz","url":"#/pages/{{{id}}}"}}""";

    public static string ContactInline(string email) =>
        $$$$"""{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Někdo","emails":["{{{{email}}}}"]}]}}}""";

    public static string Rich(params string[] inline) =>
        """{"format":"atlas-rich","version":1,"plugins":[{"id":"pages","version":1},{"id":"contacts","version":2}],"document":{"type":"doc","content":[{"type":"paragraph","content":["""
        + string.Join(",", inline.Prepend("""{"type":"text","text":"Text"}""")) + "]}]}}";

    /// <summary>A page of the space, published unless <paramref name="publish"/> is false.</summary>
    public async Task<PageDto> PageAsync(string title, UserAccount? author = null, bool publish = true, params string[] inline)
    {
        author ??= Writer;
        var page = await Pages.CreateAsync(new CreatePageRequest(SpaceId, null, title, Rich(inline), title), author, default);
        var written = publish ? await Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), author, default) : page;
        await DeriveAsync();
        return written;
    }

    public async Task<Guid> ContactAsync(UserAccount actor, string scope, string name, string? email = null)
    {
        var data = new JsonObject { ["name"] = name };
        if (email is not null)
        {
            data["emails"] = new JsonArray(email);
        }
        return (await As<ObjectDto>(actor, new CreateObject(ContactObjectType.Id, scope, scope == ObjectScopes.Space ? SpaceId : null, data))).Id;
    }

    public Task<Guid> BoardAsync(string title, UserAccount? actor = null) => Boards.CreateBoardAsync(actor ?? Writer, SpaceId, title);

    public static BoardChange PageCard(Guid id, Guid page, string? anchor = null)
    {
        var props = new JsonObject { ["display"] = "card" };
        if (anchor is not null)
        {
            props["anchor"] = anchor;
        }
        return new BoardChange("create", Item: new BoardItemInput(id, "page", 0, 0, 240, 160, 0, TargetResourceId: page, Props: props));
    }

    public static BoardChange ObjectCard(Guid id, Guid objectId) => new("create", Item: new BoardItemInput(id, "object", 0, 0, 280, 160, 0,
        Props: new JsonObject { ["objectId"] = objectId.ToString(), ["typeId"] = ContactObjectType.Id, ["view"] = "card" }));

    public async Task TrashPageAsync(PageDto page, UserAccount actor)
    {
        var tree = await As<PageTreeDto>(actor, new GetPageTree(page.SpaceId));
        await As<OrganizationChange>(actor, new TrashPage(page.Id, new TrashPageRequest(false, tree.Version, page.Version)));
        await DeriveAsync();
    }

    /// <summary>A board becomes a page's own board (a Purpose and a parent page).</summary>
    public Task AttachAsync(Guid boardId, Guid pageId) =>
        Host.ExecuteAsync($"""UPDATE "Board" SET "Purpose"='projector.deployment', "ParentPageId"='{pageId}' WHERE "Id"=:id""", boardId);

    public Task<SpaceContents> ContentsAsync(UserAccount actor, string? relation = null, string? type = null, string? q = null, string? sort = null,
        string? cursor = null) => As<SpaceContents>(actor, new ListSpaceContents(SpaceId, type, relation, q, sort, cursor));

    public Task<ObjectRelations> RelationsAsync(UserAccount actor, Guid objectId, int depth = 1, string? kinds = null) =>
        As<ObjectRelations>(actor, new GetObjectRelations(objectId, depth, kinds));

    public Task<T> As<T>(UserAccount actor, object message) => Boards.As<T>(actor, message);

    public Task As(UserAccount actor, object message) => Boards.As(actor, message);

    /// <summary>A host without the outbox's queue sends no deliveries: the page links and mentions are derived from the pages written so
    /// far, as the deliveries would have (RefreshPageDerivedAsync), after a page is written here and wherever a test reads them; a host
    /// with the queue waits for it. Only the fixture's spaces: on the shared host the other pages are other tests'.</summary>
    public Task DeriveAsync() => m_outbox ? Events.OutboxTests.DrainAsync(Host) : Host.RefreshPageDerivedAsync(SpaceId, Boards.SpaceId, Boards.OtherSpaceId);

    public ValueTask DisposeAsync() => Boards.DisposeAsync();
}
