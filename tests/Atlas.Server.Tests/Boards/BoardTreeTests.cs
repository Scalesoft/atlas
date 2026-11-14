using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Spaces.Commands;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Boards.BoardsFixture;

namespace Atlas.Server.Tests.Boards;

/// <summary>
/// Boards as nodes of the space tree (interaction spec §6): leaves under a page or at the root, in the one sibling order of pages
/// and boards, moved like pages under the tree's version, and taken along by a page subtree's trash, restore and cross-space move.
/// </summary>
public sealed class BoardTreeTests
{
    [PostgreSqlFact]
    public async Task The_tree_interleaves_boards_with_pages_and_moves_either_before_the_other()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var a = await PublishedAsync(pages, f, "A", null);
        var b = await PublishedAsync(pages, f, "B", null);
        var child = await PublishedAsync(pages, f, "Child", a);
        var root = await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Root board"));
        var under = await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Under A", a));

        // New boards are the last of their siblings, pages included.
        Assert.Equal(["A:page", "B:page", "Root board:board", "Child:page>A", "Under A:board>A"], Shape(await Tree(f, f.Reader)));

        // A board before a page, and a page before a board: one order for both.
        var moved = await f.As<OrganizationChange>(f.Writer, new MoveBoard(root.Id, new MovePageRequest(null, b, await VersionAsync(f))));
        Assert.Equal([root.Id], moved.AffectedIds);
        Assert.Equal(["A:page", "Root board:board", "B:page", "Child:page>A", "Under A:board>A"], Shape(await Tree(f, f.Reader)));
        await f.As<OrganizationChange>(f.Writer, new MovePage(child, new MovePageRequest(a, under.Id, await VersionAsync(f))));
        Assert.Equal(["A:page", "Root board:board", "B:page", "Child:page>A", "Under A:board>A"], Shape(await Tree(f, f.Reader)));
        await f.As<OrganizationChange>(f.Writer, new MoveBoard(under.Id, new MovePageRequest(a, child, await VersionAsync(f))));
        Assert.Equal(["A:page", "Root board:board", "B:page", "Under A:board>A", "Child:page>A"], Shape(await Tree(f, f.Reader)));
        // Under a page, to the root and back; a reader sees but cannot change it.
        await f.As<OrganizationChange>(f.Writer, new MoveBoard(under.Id, new MovePageRequest(null, null, await VersionAsync(f))));
        var tree = await Tree(f, f.Reader);
        Assert.Equal(["A:page", "Root board:board", "B:page", "Under A:board", "Child:page>A"], Shape(tree));
        Assert.All(tree.Items.Where(item => item.Type == "board"), item => Assert.False(item.CanEdit));
        Assert.All((await Tree(f, f.Writer)).Items.Where(item => item.Type == "board"), item => Assert.True(item.CanEdit));
    }

    [PostgreSqlFact]
    public async Task A_board_move_keeps_the_rules_of_a_page_move()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var a = await PublishedAsync(pages, f, "A", null);
        var board = await f.CreateBoardAsync();
        var stale = await VersionAsync(f);
        await f.As<OrganizationChange>(f.Writer, new MoveBoard(board, new MovePageRequest(a, null, stale)));
        // Every move counts in the tree's version, so a move against the old one is a conflict.
        await Refused(409, () => f.As<OrganizationChange>(f.Writer, new MoveBoard(board, new MovePageRequest(null, null, stale))), "tree_conflict");
        var hidden = (await pages.CreateAsync(new CreatePageRequest(f.OtherSpaceId, null, "Elsewhere", IndexFixture.Rich("x"), "x"), f.Admin, default)).Id;
        foreach (var parent in new Guid?[] { hidden, Guid.NewGuid(), board })
        {
            await Refused(400, () => f.As<OrganizationChange>(f.Writer, new MoveBoard(board, new MovePageRequest(parent, null, stale + 1))));
        }
        await Refused(400, () => f.As<OrganizationChange>(f.Writer, new MoveBoard(board, new MovePageRequest(a, Guid.NewGuid(), stale + 1))));
        await Refused(403, () => f.As<OrganizationChange>(f.Reader, new MoveBoard(board, new MovePageRequest(null, null, stale + 1))));
        await Refused(404, () => f.As<OrganizationChange>(f.Outsider, new MoveBoard(board, new MovePageRequest(null, null, stale + 1))));
        await Refused(400, () => f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Nowhere", hidden)));
    }

    [PostgreSqlFact]
    public async Task A_page_subtree_takes_its_boards_to_the_trash_and_back_and_a_board_alone_comes_back_by_itself()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var a = await PublishedAsync(pages, f, "A", null);
        var child = await PublishedAsync(pages, f, "Child", a);
        var underA = (await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Under A", a))).Id;
        var underChild = (await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Under child", child))).Id;
        var alone = await f.CreateBoardAsync(title: "Alone");
        var subscriber = f.Subscribe(f.Reader, underChild);

        await f.As(f.Writer, new DeleteBoard(alone));
        var version = (await pages.GetAsync(a, f.Writer, default)).Version;
        var trashed = await f.As<OrganizationChange>(f.Writer, new TrashPage(a, new TrashPageRequest(true, await VersionAsync(f), version)));
        Assert.Equal(new[] { a, child }.Order(), trashed.AffectedIds.Order());
        Assert.Empty((await Tree(f, f.Admin)).Items);
        var trash = await f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, 0, 50));
        Assert.Equal(["A:page", "Alone:board", "Child:page", "Under A:board", "Under child:board"], trash.Items.Select(item => item.Title + ":" + item.Type).Order());
        var batch = trash.Items.Single(item => item.Id == a).TrashBatchId;
        Assert.All(trash.Items.Where(item => item.Id == underA || item.Id == underChild), item => Assert.Equal(batch, item.TrashBatchId));
        Assert.NotEqual(batch, trash.Items.Single(item => item.Id == alone).TrashBatchId);
        // A board in the trash is gone for its live subscribers, as a deleted board is.
        Assert.Contains(f.Hub.Sent, sent => sent.Method == "Revoked" && sent.Connections.Contains(subscriber));
        await Refused(404, () => f.As<BoardDto>(f.Writer, new GetBoard(underA)));

        await f.As<OrganizationChange>(f.Admin, new UntrashPage(a, new UntrashPageRequest(null, true, await VersionAsync(f))));
        Assert.Equal(["A:page", "Child:page>A", "Under A:board>A", "Under child:board>Child"], Shape(await Tree(f, f.Admin)).Order());
        Assert.Equal(["Alone:board"], (await f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, 0, 50))).Items.Select(item => item.Title + ":" + item.Type));

        // The lone board comes back by itself, under the space's administration as a page does.
        await Refused(403, async () => await f.As<OrganizationChange>(f.Writer, new UntrashBoard(alone, new UntrashPageRequest(null, false, await VersionAsync(f)))));
        await f.As<OrganizationChange>(f.Admin, new UntrashBoard(alone, new UntrashPageRequest(child, false, await VersionAsync(f))));
        Assert.Contains("Alone:board>Child", Shape(await Tree(f, f.Admin)));
        Assert.Equal("Alone", (await f.As<BoardDto>(f.Reader, new GetBoard(alone))).Title);
    }

    [PostgreSqlFact]
    public async Task A_space_in_the_trash_takes_its_boards_along_the_root_ones_too_and_brings_back_only_its_batch()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var a = await PublishedAsync(pages, f, "A", null);
        await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Under A", a));
        await f.CreateBoardAsync(title: "Root");
        var deleted = await f.CreateBoardAsync(title: "Deleted before");
        await f.As(f.Writer, new DeleteBoard(deleted));

        await f.As(f.Admin, new TrashSpace(f.SpaceId, new("Boards")));
        await Refused(404, () => f.As<BoardDto>(f.Admin, new GetBoard(deleted)));
        using (var session = f.Host.OpenSession())
        {
            Assert.Equal(0L, await session.CreateSQLQuery("""SELECT count(*) FROM "Board" WHERE NOT "IsTrashed" AND "SpaceId"=:space""").SetParameter("space", f.SpaceId).UniqueResultAsync<long>());
        }

        await f.As(f.Admin, new RestoreSpace(f.SpaceId));
        Assert.Equal(["A:page", "Root:board", "Under A:board>A"], Shape(await Tree(f, f.Admin)).Order());
        Assert.Equal(["Deleted before:board"], (await f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, 0, 50))).Items.Select(item => item.Title + ":" + item.Type));
    }

    [PostgreSqlFact]
    public async Task A_page_moved_to_another_space_takes_its_boards_and_their_identity_along()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var a = await PublishedAsync(pages, f, "A", null);
        var board = (await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Under A", a))).Id;
        var page = await pages.GetAsync(a, f.Admin, default);
        await f.As<CrossSpaceChange>(f.Admin, new MovePageToSpace(a, new MovePageSpaceRequest(f.OtherSpaceId, null, null, true,
            await VersionAsync(f), await VersionAsync(f, f.OtherSpaceId), page.Version, true)));

        Assert.Equal(["A:page", "Under A:board>A"], Shape(await Tree(f, f.Admin, f.OtherSpaceId)));
        Assert.Empty((await Tree(f, f.Admin)).Items);
        // The board's space is the target's: its readers lost it, the resource row followed.
        await Refused(404, () => f.As<BoardDto>(f.Reader, new GetBoard(board)));
        Assert.Equal(f.OtherSpaceId, (await f.As<BoardDto>(f.Admin, new GetBoard(board))).SpaceId);
        using var session = f.Host.OpenSession();
        Assert.Equal(("atlas.board", (Guid?)f.OtherSpaceId), (await session.GetAsync<ContentResource>(board)) is { } resource ? (resource.TypeId, resource.SpaceId) : default);
    }

    [PostgreSqlFact]
    public async Task Without_the_module_the_tree_and_the_trash_are_the_pages_alone_and_board_rows_stay()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_tree_wiki_");
        var writer = await host.SeedUserAsync("writer");
        var space = await host.SeedSpaceAsync("Wiki", (writer, SpaceRole.Writer));
        var pages = new TestPages(host);
        var a = await pages.CreateAsync(new CreatePageRequest(space, null, "A", IndexFixture.Rich("x"), "x"), writer, default);
        // A board left by the module before it was switched off, under the page.
        await host.ExecuteAsync("""
            INSERT INTO "Board"("Id","SpaceId","ParentPageId","SortOrder","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed")
            SELECT gen_random_uuid(), p."SpaceId", p."Id", 1024, 'Kept', 0, now(), p."OwnerId", now(), false FROM "Page" p WHERE p."Id"=:id
            """, a.Id);
        host.Actor.Current = writer;
        var tree = await host.InvokeAsync<PageTreeDto>(new GetPageTree(space));
        Assert.Equal(["A:page"], tree.Items.Select(item => item.Title + ":" + item.Type));
        await host.InvokeAsync<OrganizationChange>(new TrashPage(a.Id, new TrashPageRequest(true, tree.Version, a.Version)));
        using var session = host.OpenSession();
        Assert.Equal(1L, await session.CreateSQLQuery("""SELECT count(*) FROM "Board" WHERE NOT "IsTrashed" AND "Title"='Kept' AND "SpaceId"=:space""")
            .SetParameter("space", space).UniqueResultAsync<long>());
    }

    private static async Task<Guid> PublishedAsync(TestPages pages, BoardsFixture f, string title, Guid? parent)
    {
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, parent, title, IndexFixture.Rich(title), title), f.Writer, default);
        await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        return page.Id;
    }

    private static Task<PageTreeDto> Tree(BoardsFixture f, UserAccount actor, Guid? space = null)
    {
        return f.As<PageTreeDto>(actor, new GetPageTree(space ?? f.SpaceId));
    }

    private static async Task<long> VersionAsync(BoardsFixture f, Guid? space = null)
    {
        return (await Tree(f, f.Admin, space)).Version;
    }

    /// <summary>The tree in its order: "title:type", with "&gt;parent title" under a parent.</summary>
    private static string[] Shape(PageTreeDto tree)
    {
        var titles = tree.Items.ToDictionary(item => item.Id, item => item.Title);
        var roots = tree.Items.Where(item => item.ParentId is null).Select(item => $"{item.Title}:{item.Type}");
        var children = tree.Items.Where(item => item.ParentId is not null).Select(item => $"{item.Title}:{item.Type}>{titles[item.ParentId!.Value]}");
        return roots.Concat(children).ToArray();
    }
}
