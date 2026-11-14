using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Queries;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Server.Tests.Comments;

/// <summary>
/// The boards host (pages and boards, a recording hub) with a discussion space: <see cref="Manager"/> is its Admin, the fixture's
/// Writer writes and its Reader reads there; the Outsider has no role. Comments go through the messages the endpoints send.
/// </summary>
internal sealed class CommentsFixture : IAsyncDisposable
{
    private CommentsFixture(BoardsFixture boards)
    {
        Boards = boards;
    }

    public BoardsFixture Boards { get; }
    public AtlasTestHost Host => Boards.Host;
    public UserAccount Manager { get; private set; } = null!;
    public UserAccount Writer => Boards.Writer;
    public UserAccount Reader => Boards.Reader;
    public UserAccount Outsider => Boards.Outsider;
    public UserAccount Admin => Boards.Admin;
    public Guid SpaceId { get; private set; }
    public ViewSessions Views => Host.Services.GetRequiredService<ViewSessions>();

    /// <summary>The fixture on the boards host every test of the process shares (BoardsFixture.SharedAsync).</summary>
    public static Task<CommentsFixture> SharedAsync() => CreateAsync(BoardsFixture.SharedAsync());

    /// <summary>A host of the test's own.</summary>
    /// <param name="outbox">A host whose outbox delivers (BoardsFixture).</param>
    public static Task<CommentsFixture> CreateAsync(Action<IServiceCollection>? configure = null, bool outbox = false) =>
        CreateAsync(BoardsFixture.CreateAsync(configure, outbox));

    private static async Task<CommentsFixture> CreateAsync(Task<BoardsFixture> boards)
    {
        var fixture = new CommentsFixture(await boards);
        fixture.Manager = await fixture.Host.SeedUserAsync("manager");
        fixture.SpaceId = await fixture.Host.SeedSpaceAsync("Discussion", (fixture.Manager, SpaceRole.Admin), (fixture.Writer, SpaceRole.Writer),
            (fixture.Reader, SpaceRole.Reader));
        return fixture;
    }

    public Task<Guid> BoardAsync(string title = "Plan") => Boards.CreateBoardAsync(Writer, SpaceId, title);

    public async Task<Guid> PageAsync(string title, string text)
    {
        var pages = new TestPages(Host);
        var page = await pages.CreateAsync(new CreatePageRequest(SpaceId, null, title, IndexFixture.Rich(text), text), Writer, default);
        await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), Writer, default);
        return page.Id;
    }

    public Task<CommentDto> PostAsync(UserAccount actor, Guid objectId, string body, string? anchor = null)
    {
        return Boards.As<CommentDto>(actor, new PostComment(objectId, body, anchor));
    }

    public Task<CommentPage> PageOfAsync(UserAccount actor, Guid objectId, Guid? before = null)
    {
        return Boards.As<CommentPage>(actor, new GetComments(objectId, before));
    }

    public Task As(UserAccount actor, object message) => Boards.As(actor, message);

    public Task<T> As<T>(UserAccount actor, object message) => Boards.As<T>(actor, message);

    /// <summary>A connection as LiveHub registers one at its handshake.</summary>
    public string Connect(UserAccount account) => Boards.Connect(account);

    public int Mark() => Boards.Hub.Sent.Count;

    public T[] Since<T>(int mark, string connection, string method) => Boards.Hub.Sent.Skip(mark)
        .Where(sent => sent.Connections.Contains(connection) && sent.Method == method).Select(sent => (T)sent.Payload!).ToArray();

    public ValueTask DisposeAsync() => Boards.DisposeAsync();
}
