using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Features.TreeReferences.Commands;
using Atlas.Server.Features.TreeReferences.Contracts;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Hub;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Boards.BoardsFixture;

namespace Atlas.Server.Tests.TreeReferences;

/// <summary>
/// Reference nodes of the space tree (spec 2026-10-27 §13): a placement of any readable object, shown with the target's title only to
/// whom may read the target (a lock otherwise), changed by a Writer of the space, and taken along by its page subtree's trash, restore
/// and cross-space move. Removing one removes the link alone.
/// </summary>
public sealed class TreeReferenceTests
{
    [PostgreSqlFact]
    [Trait("Category", "Smoke")]
    public async Task A_reference_shows_its_target_as_the_reader_may_read_it_and_only_a_writer_changes_it()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var a = await PublishedAsync(pages, f, f.Writer, f.SpaceId, "A");
        var b = await PublishedAsync(pages, f, f.Writer, f.SpaceId, "B");
        var elsewhere = await PublishedAsync(pages, f, f.Admin, f.OtherSpaceId, "Elsewhere");
        var otherBoard = await f.CreateBoardAsync(f.Admin, f.OtherSpaceId, "Other board");
        var jana = await f.As<ObjectDto>(f.Writer, Contact("""{"name":"**Jana** Nováková"}""", ObjectScopes.Wiki));
        var mine = await f.As<ObjectDto>(f.Reader, Contact("""{"name":"Private"}""", ObjectScopes.Private));

        // A wiki contact at the root and a section of page A under it; the node is a leaf in the one sibling order.
        var contact = await f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, jana.Id));
        Assert.Equal(("reference", "Jana Nováková", (Guid?)jana.Id, "atlas.contact", true), (contact.Type, contact.Title, contact.TargetId, contact.TargetTypeId, contact.Readable));
        var section = await f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, a, "uvod", a));
        await f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, b, before: b));
        // The wiki administrator places what a reader of this space cannot read: a page and a board of another space.
        await f.As<PageTreeItem>(f.Admin, Add(f.SpaceId, elsewhere));
        await f.As<PageTreeItem>(f.Admin, Add(f.SpaceId, otherBoard));

        var reader = await Tree(f, f.Reader);
        Assert.Equal(["A:page", "B:reference", "B:page", "Jana Nováková:reference", "🔒:reference", "🔒:reference", "A:reference>A"], Shape(reader));
        Assert.Equal("uvod", reader.Items.Single(item => item.Id == section.Id).Anchor);
        Assert.All(reader.Items.Where(item => item.Title is null), item => Assert.Equal((false, (string?)null), (item.Readable, item.TargetTypeId)));
        Assert.All(reader.Items.Where(item => item.Type == "reference"), item => Assert.False(item.CanEdit));
        var admin = (await Tree(f, f.Admin)).Items.Where(item => item.Type == "reference").ToArray();
        Assert.Equal(["A", "B", "Elsewhere", "Jana Nováková", "Other board"], admin.Select(item => item.Title!).Order());
        Assert.Equal(["atlas.board", "atlas.contact", "atlas.page"], admin.Select(item => item.TargetTypeId!).Distinct().Order());

        // Placing one needs Writer in the space and a target the creator may read.
        await Refused(403, () => f.As<PageTreeItem>(f.Reader, Add(f.SpaceId, jana.Id)));
        await Refused(404, () => f.As<PageTreeItem>(f.Outsider, Add(f.SpaceId, jana.Id)));
        await Refused(404, () => f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, elsewhere)), "target_not_found");
        await Refused(404, () => f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, mine.Id)), "target_not_found");
        await Refused(404, () => f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, Guid.NewGuid())), "target_not_found");
        await Refused(400, () => f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, a, "<script>")), "invalid_anchor");
        await Refused(400, () => f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, a, parent: elsewhere)));

        // Moved as a page is moved, under the tree's version; a reader cannot, an outsider does not see it.
        var version = await VersionAsync(f);
        var moved = await f.As<OrganizationChange>(f.Writer, new MoveTreeReference(contact.Id, new MovePageRequest(b, null, version)));
        Assert.Equal([contact.Id], moved.AffectedIds);
        Assert.Contains("Jana Nováková:reference>B", Shape(await Tree(f, f.Reader)));
        await Refused(409, () => f.As<OrganizationChange>(f.Writer, new MoveTreeReference(contact.Id, new MovePageRequest(null, null, version))), "tree_conflict");
        await Refused(403, () => f.As<OrganizationChange>(f.Reader, new MoveTreeReference(contact.Id, new MovePageRequest(null, null, moved.Version))));

        // Removing it removes the link only.
        await Refused(403, () => f.As(f.Reader, new RemoveTreeReference(contact.Id)));
        await Refused(404, () => f.As(f.Outsider, new RemoveTreeReference(contact.Id)));
        await f.As(f.Writer, new RemoveTreeReference(contact.Id));
        Assert.DoesNotContain((await Tree(f, f.Reader)).Items, item => item.Id == contact.Id);
        Assert.Equal("Jana Nováková", (await f.As<ObjectDto>(f.Reader, new GetObject(jana.Id))).Title);
        await Refused(404, () => f.As(f.Writer, new RemoveTreeReference(contact.Id)));
    }

    [PostgreSqlFact]
    public async Task A_page_subtree_takes_its_references_to_the_trash_back_and_to_another_space()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var a = await PublishedAsync(pages, f, f.Writer, f.SpaceId, "A");
        var child = await PublishedAsync(pages, f, f.Writer, f.SpaceId, "Child", a);
        var target = await PublishedAsync(pages, f, f.Writer, f.SpaceId, "Target");
        await f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, target, parent: child));

        var page = await pages.GetAsync(a, f.Writer, default);
        await f.As<OrganizationChange>(f.Writer, new TrashPage(a, new TrashPageRequest(true, await VersionAsync(f), page.Version)));
        Assert.Equal(["Target:page"], Shape(await Tree(f, f.Admin)));
        // The space's trash lists pages and boards: a node comes back with its batch, never alone.
        Assert.DoesNotContain((await f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, 0, 50))).Items, item => item.Type == "reference");

        await f.As<OrganizationChange>(f.Admin, new UntrashPage(a, new UntrashPageRequest(null, true, await VersionAsync(f))));
        Assert.Contains("Target:reference>Child", Shape(await Tree(f, f.Admin)));

        page = await pages.GetAsync(a, f.Admin, default);
        await f.As<CrossSpaceChange>(f.Admin, new MovePageToSpace(a, new MovePageSpaceRequest(f.OtherSpaceId, null, null, true,
            await VersionAsync(f), await VersionAsync(f, f.OtherSpaceId), page.Version, true)));
        Assert.Equal(["A:page", "Child:page>A", "Target:reference>Child"], Shape(await Tree(f, f.Admin, f.OtherSpaceId)).Order());
        // A target left behind stays readable from the other space to whom reads it.
        Assert.Equal(["Target:page"], Shape(await Tree(f, f.Admin)));
    }

    [PostgreSqlFact]
    public async Task A_target_read_through_a_disabled_hub_group_becomes_a_lock()
    {
        // A host of its own: seeds the installation's one Hub connection (UX_HubConnectionRevision_Singleton), which every test of a shared host would run under.
        await using var f = await BoardsFixture.CreateAsync();
        var (member, group, _) = await HubEffectiveAuthorityTests.SeedAsync(f.Host, false);
        await f.Host.SeedAsync(async session =>
        {
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = member.Id, Role = (int)SpaceRole.Reader });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.OtherSpaceId, GroupId = group, Role = (int)SpaceRole.Reader });
        });
        var board = await f.CreateBoardAsync(f.Admin, f.OtherSpaceId, "Other board");
        var reference = await f.As<PageTreeItem>(f.Admin, Add(f.SpaceId, board));
        Assert.Equal("Other board", (await Tree(f, member)).Items.Single(item => item.Id == reference.Id).Title);

        // The group still lists the member (GroupMember), but its Hub binding is off (EffectiveGroupMember): no role in the other space.
        await f.Host.ExecuteAsync("""UPDATE "HubGroupBinding" SET "Enabled"=false WHERE "GroupId"=:id""", group);

        Assert.Equal((false, (string?)null), (await Tree(f, member)).Items.Where(item => item.Id == reference.Id).Select(item => (item.Readable, item.Title)).Single());
    }

    [PostgreSqlFact]
    public async Task The_tree_reads_the_targets_of_any_number_of_references_in_one_statement()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var a = await PublishedAsync(pages, f, f.Writer, f.SpaceId, "A");
        await f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, a));
        await Tree(f, f.Reader);
        var one = await RecordAsync(f);
        for (var i = 0; i < 5; i++)
        {
            var contact = await f.As<ObjectDto>(f.Writer, Contact($$"""{"name":"Contact {{i}}"}""", ObjectScopes.Wiki));
            await f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, contact.Id, parent: a));
        }
        await f.As<PageTreeItem>(f.Writer, Add(f.SpaceId, await f.CreateBoardAsync(f.Writer)));

        Assert.Equal(one, await RecordAsync(f));
    }

    /// <summary>SEC-Z-3: taking a node from under a page changes that page's children as placing one there does, so a reference node and a
    /// board leave a page (moved, removed or trashed) only by whom may edit it. A Writer who joined the space after the page took its own access does not read it
    /// and sees them at the root (D8), but they are missing to him; once he may read the page, he is refused.</summary>
    [PostgreSqlFact]
    public async Task A_node_under_a_page_of_its_own_access_leaves_it_only_by_whom_may_edit_that_page()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var closed = await PublishedAsync(pages, f, f.Admin, f.SpaceId, "Uzavřená");
        var jana = await f.As<ObjectDto>(f.Admin, Contact("""{"name":"Jana"}""", ObjectScopes.Wiki));
        var reference = await f.As<PageTreeItem>(f.Admin, Add(f.SpaceId, jana.Id, parent: closed));
        var board = await f.As<BoardDto>(f.Admin, new CreateBoard(f.SpaceId, "Plán", closed));
        await f.As<PageAccessModeResult>(f.Admin, new SetPageAccessMode(closed,
            new UpdatePageAccessMode(true, (await f.As<PageAccessState>(f.Admin, new GetPageAccess(closed))).ModeVersion)));
        var newcomer = await f.Host.SeedUserAsync("newcomer");
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = newcomer.Id, Role = (int)SpaceRole.Writer }));

        var version = await VersionAsync(f);
        await Refused(404, () => f.As(newcomer, new RemoveTreeReference(reference.Id)));
        await Refused(404, () => f.As<OrganizationChange>(newcomer, new MoveTreeReference(reference.Id, new MovePageRequest(null, null, version))));
        await Refused(404, () => f.As<OrganizationChange>(newcomer, new MoveBoard(board.Id, new MovePageRequest(null, null, version))));
        await Refused(404, () => f.As(newcomer, new DeleteBoard(board.Id)));
        await f.As(f.Admin, new SetPageGrant(closed, new UpdatePageGrant(newcomer.Id, null, "Reader", null)));
        await Refused(403, () => f.As(newcomer, new RemoveTreeReference(reference.Id)));
        await Refused(403, () => f.As<OrganizationChange>(newcomer, new MoveBoard(board.Id, new MovePageRequest(null, null, version))));
        // The trash takes a board from under its page as a move does.
        await Refused(403, () => f.As(newcomer, new DeleteBoard(board.Id)));
        Assert.Equal(["Jana:reference>Uzavřená", "Plán:board>Uzavřená"], Shape(await Tree(f, f.Admin)).Where(x => x.EndsWith(">Uzavřená", StringComparison.Ordinal)).Order());

        // Whom may edit the page takes them out.
        await f.As<OrganizationChange>(f.Writer, new MoveBoard(board.Id, new MovePageRequest(null, null, await VersionAsync(f))));
        await f.As(f.Writer, new RemoveTreeReference(reference.Id));
    }

    private static async Task<int> RecordAsync(BoardsFixture f)
    {
        using var recording = SqlRecording.Start();
        await Tree(f, f.Reader);
        return recording.Statements.Count;
    }

    private static AddTreeReference Add(Guid space, Guid target, string? anchor = null, Guid? parent = null, Guid? before = null)
    {
        return new AddTreeReference(space, new AddTreeReferenceRequest(target, anchor, parent, before));
    }

    private static CreateObject Contact(string data, string scope)
    {
        return new CreateObject(ContactObjectType.Id, scope, null, JsonNode.Parse(data)!.AsObject());
    }

    private static async Task<Guid> PublishedAsync(TestPages pages, BoardsFixture f, UserAccount author, Guid space, string title, Guid? parent = null)
    {
        var page = await pages.CreateAsync(new CreatePageRequest(space, parent, title, IndexFixture.Rich(title), title), author, default);
        await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), author, default);
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

    /// <summary>The tree in its order: "title:type" (🔒 for an unreadable target), with "&gt;parent title" under a parent.</summary>
    private static string[] Shape(PageTreeDto tree)
    {
        var titles = tree.Items.ToDictionary(item => item.Id, item => item.Title ?? "🔒");
        var roots = tree.Items.Where(item => item.ParentId is null).Select(item => $"{titles[item.Id]}:{item.Type}");
        var children = tree.Items.Where(item => item.ParentId is not null).Select(item => $"{titles[item.Id]}:{item.Type}>{titles[item.ParentId!.Value]}");
        return roots.Concat(children).ToArray();
    }
}
