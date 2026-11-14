using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.PageLinks.Queries;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Boards.BoardsFixture;

namespace Atlas.Server.Tests.Boards;

/// <summary>Boards in the personal library and in search, beside pages (interaction spec §6), with the rules of their space.</summary>
public sealed class BoardLibrarySearchTests
{
    [PostgreSqlFact]
    public async Task Favorites_and_recent_visits_hold_boards_beside_pages_for_who_may_read_them()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Plan page", IndexFixture.Rich("x"), "x"), f.Writer, default);
        await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        var board = await f.CreateBoardAsync(title: "Roadmap");

        Assert.True((await f.As<PersonalPageState>(f.Reader, new SetPageFavorite(page.Id, new SetFavorite(true)))).IsFavorite);
        Assert.True((await f.As<PersonalPageState>(f.Reader, new SetPageFavorite(board, new SetFavorite(true)))).IsFavorite);
        Assert.NotNull((await f.As<PersonalPageState>(f.Reader, new RecordPageVisit(board))).VisitedAt);
        Assert.Equal((true, true), (await f.As<PersonalPageState>(f.Reader, new GetPersonalPageState(board))) is { } state ? (state.IsFavorite, state.VisitedAt is not null) : default);

        var favorites = (await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("favorites", 0, 20))).Items;
        Assert.Equal(["Roadmap:board", "Plan page:page"], favorites.Select(item => item.Title + ":" + item.Type));
        var item = favorites[0];
        Assert.Equal((board, f.SpaceId, "Boards", (Guid?)null, true), (item.Id, item.SpaceId, item.SpaceName, item.ParentId, item.IsFavorite));
        Assert.Equal(["Roadmap:board"], (await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("recent", 0, 20))).Items.Select(x => x.Title + ":" + x.Type));
        // Paged as one list.
        var first = await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("favorites", 0, 1));
        Assert.Equal(("Roadmap", true, 1), (Assert.Single(first.Items).Title, first.HasMore, first.NextOffset));
        Assert.Equal("Plan page", Assert.Single((await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("favorites", 1, 1))).Items).Title);

        // Who may not read the board cannot mark it, a board in the trash leaves the lists, and a visit is not a page view.
        await Refused(404, () => f.As<PersonalPageState>(f.Outsider, new SetPageFavorite(board, new SetFavorite(true))));
        await Refused(404, () => f.As<PersonalPageState>(f.Reader, new RecordPageVisit(Guid.NewGuid())));
        await f.As(f.Writer, new DeleteBoard(board));
        Assert.Equal(["Plan page:page"], (await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("favorites", 0, 20))).Items.Select(x => x.Title + ":" + x.Type));
        Assert.Empty((await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("recent", 0, 20))).Items);
        using var session = f.Host.OpenSession();
        Assert.Equal(0L, await session.CreateSQLQuery("""SELECT count(*) FROM "PageView" WHERE "PageId"=:id""").SetParameter("id", board).UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Search_finds_a_board_by_its_title_and_its_texts_for_readers_of_its_space()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var board = await f.CreateBoardAsync(title: "Quarterly roadmap");
        Guid note = Guid.NewGuid(), contact = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, board, Note(note, "Kickoff with the **customer**"),
            new BoardChange("create", Item: new BoardItemInput(contact, "block", 300, 0, 240, 160, 1, Props: JsonNode.Parse(
                """{"block":{"type":"contactCard","attrs":{"data":{"version":1,"contacts":[{"name":"Jana Nováková","title":"Account manager"}]}}}}""")!.AsObject())));

        var hit = Assert.Single(await FindAsync(f, f.Reader, "kickoff"));
        Assert.Equal(("board", board, f.SpaceId, "Boards", "Quarterly roadmap"), (hit.Type, hit.Id, hit.SpaceId, hit.SpaceName, hit.Title));
        Assert.Contains("Kickoff", hit.Snippet);
        // The block's text is the one a page shows of it; the title counts too; prefixes and accents as for pages.
        Assert.Equal(board, Assert.Single(await FindAsync(f, f.Reader, "novakova")).Id);
        Assert.Equal(board, Assert.Single(await FindAsync(f, f.Reader, "quarter")).Id);
        Assert.Empty(await FindAsync(f, f.Outsider, "kickoff"));

        // The document follows every batch that changes a text: the old word is gone, the new one found.
        await f.ApplyAsync(f.Writer, board, new BoardChange("set", ItemId: note, Text: "Retrospective"));
        Assert.Empty(await FindAsync(f, f.Reader, "kickoff"));
        Assert.Single(await FindAsync(f, f.Reader, "retrospective"));
        await f.As(f.Writer, new DeleteBoard(board));
        Assert.Empty(await FindAsync(f, f.Reader, "retrospective"));
    }

    [PostgreSqlFact]
    public async Task A_board_link_in_a_page_resolves_to_the_board_title_for_readers_of_its_space()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var page = await new TestPages(f.Host).CreateAsync(new CreatePageRequest(f.SpaceId, null, "Plan page", IndexFixture.Rich("x"), "x"), f.Writer, default);
        var board = await f.CreateBoardAsync(title: "Roadmap");
        Guid[] ids = [page.Id, board, Guid.NewGuid()];

        // One batch answers pages and boards; a page keeps its shape (no type), a board says what it is.
        Assert.Equal([new PageReference(page.Id, "Plan page"), new PageReference(board, "Roadmap", "board")],
            (await f.As<PageReferenceList>(f.Writer, new ResolvePageReferences(ids))).Items);
        Assert.Equal([new PageReference(board, "Roadmap", "board")], (await f.As<PageReferenceList>(f.Reader, new ResolvePageReferences([board]))).Items);
        Assert.Empty((await f.As<PageReferenceList>(f.Outsider, new ResolvePageReferences([board]))).Items);
        await f.As(f.Writer, new DeleteBoard(board));
        Assert.Empty((await f.As<PageReferenceList>(f.Reader, new ResolvePageReferences([board]))).Items);
    }

    private static async Task<IReadOnlyList<SearchResourceHit>> FindAsync(BoardsFixture f, UserAccount actor, string query)
    {
        return (await f.As<SearchResponse>(actor, new Atlas.Server.Features.Search.Queries.Search(query, null, 0, 20, new SearchOptions()))).Resources ?? [];
    }
}
