using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Tests.PersonalLibrary;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageDecorations;

public sealed class PageDecorationHandlerTests
{
    [PostgreSqlFact]
    public async Task Set_change_and_reset_keep_an_independent_token_and_do_not_edit_the_document()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Decorated");
        Assert.Null((await f.As<PageDecorationState>(f.Writer, new GetPageDecoration(page.Id))).Decoration);
        Assert.Null((await Save(f, page.Id, null, null)).Decoration);
        var first = (await Save(f, page.Id, "folder", "blue")).Decoration!;
        Assert.NotEqual(Guid.Empty, first.RevisionId);
        Assert.Equal("folder", first.Icon);
        Assert.Equal("blue", first.Color);
        Assert.Equal(first, (await Save(f, page.Id, "folder", "blue", first.RevisionId)).Decoration);
        Assert.Equal(first, (await f.Pages.GetAsync(page.Id, f.Writer, default)).Decoration);
        var changed = (await Save(f, page.Id, "book-open", "teal", first.RevisionId)).Decoration!;
        Assert.NotEqual(first.RevisionId, changed.RevisionId);
        var reset = (await Save(f, page.Id, null, null, changed.RevisionId)).Decoration!;
        Assert.Null(reset.Icon);
        Assert.Null(reset.Color);
        Assert.NotEqual(changed.RevisionId, reset.RevisionId);
        Assert.Equal(reset, (await f.As<PageDecorationState>(f.Writer, new GetPageDecoration(page.Id))).Decoration);
        Assert.Equal(reset, (await Save(f, page.Id, null, null, reset.RevisionId)).Decoration);
        await Denied(409, () => Save(f, page.Id, "home", "rose"));
        await Denied(409, () => Save(f, page.Id, "home", "rose", first.RevisionId));
        var restored = (await Save(f, page.Id, "folder", "blue", reset.RevisionId)).Decoration!;
        Assert.NotEqual(first.RevisionId, restored.RevisionId);
        using var session = f.Host.OpenSession();
        Assert.Equal(1, (await session.GetAsync<Page>(page.Id)).Version);
        Assert.Equal(1L, await session.CreateSQLQuery("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=:id").SetParameter("id", page.Id).UniqueResultAsync<long>());
        Assert.Equal(1L, await session.CreateSQLQuery("SELECT count(*) FROM \"PageDecoration\" WHERE \"PageId\"=:id").SetParameter("id", page.Id).UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Picker_icons_and_palette_colors_save_alongside_legacy_values()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Appearance catalog");
        var legacy = (await Save(f, page.Id, "folder", "blue")).Decoration!;
        var selected = (await Save(f, page.Id, "plug", "teal-2", legacy.RevisionId)).Decoration!;
        Assert.Equal("plug", selected.Icon);
        Assert.Equal("teal-2", selected.Color);
        Assert.Equal(selected, (await f.As<PageDecorationState>(f.Writer, new GetPageDecoration(page.Id))).Decoration);
        // The longest name of the icon pack: 38 characters.
        var packed = (await Save(f, page.Id, "triangles-centerline-dashed-horizontal", "teal-2", selected.RevisionId)).Decoration!;
        Assert.Equal("triangles-centerline-dashed-horizontal", packed.Icon);
        Assert.Equal(packed, (await f.As<PageDecorationState>(f.Writer, new GetPageDecoration(page.Id))).Decoration);
    }

    [PostgreSqlFact]
    public async Task Concurrent_writers_cannot_overwrite_a_newer_decoration()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Concurrent");
        f.Host.Actor.Current = f.Writer;
        var statuses = await Task.WhenAll(new[] { "star", "home" }.Select(async icon =>
        {
            try
            {
                await f.Host.InvokeAsync<PageDecorationState>(new SetPageDecoration(page.Id, new(icon, "violet", null)));
                return 200;
            }
            catch (ApiException exception) { return exception.Status; }
        }));
        Assert.Equal([200, 409], statuses.Order());
        Assert.NotNull((await f.As<PageDecorationState>(f.Writer, new GetPageDecoration(page.Id))).Decoration);
    }

    [PostgreSqlFact]
    public async Task Invalid_icon_color_pairs_are_rejected_without_changing_the_current_selection()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Validation");
        var state = (await Save(f, page.Id, "shield-check", "green")).Decoration!;
        foreach (var (icon, color) in new (string?, string?)[]
                 {
                     ("<svg onload=alert(1)>", "blue"), ("folder", "url(red)"), ("FOLDER", "blue"),
                     ("folder", "#fff"), ("folder", null), (null, "blue"), ("", ""), (" folder", "blue")
                 })
            await Denied(400, () => Save(f, page.Id, icon, color, state.RevisionId));
        Assert.Equal(state, (await f.As<PageDecorationState>(f.Writer, new GetPageDecoration(page.Id))).Decoration);
    }

    [PostgreSqlFact]
    public async Task Visibility_and_writer_rights_guard_metadata_and_page_tree_projections()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Private");
        var decoration = (await Save(f, page.Id, "database", "amber")).Decoration!;
        foreach (var actor in new[] { f.Reader, f.OtherWriter })
        {
            await Denied(404, () => f.As<PageDecorationState>(actor, new GetPageDecoration(page.Id)));
            await Denied(404, () => f.As<PageDecorationState>(actor, new SetPageDecoration(page.Id, new("star", "rose", decoration.RevisionId))));
            Assert.Empty((await f.As<PageTreeDto>(actor, new GetPageTree(f.SpaceId))).Items);
        }
        Assert.Equal(decoration, (await f.As<PageDecorationState>(f.Admin, new GetPageDecoration(page.Id))).Decoration);
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        await Denied(403, () => f.As<PageDecorationState>(f.Reader, new SetPageDecoration(page.Id, new("star", "rose", decoration.RevisionId))));
        Assert.Equal(decoration, (await f.Pages.GetAsync(page.Id, f.Reader, default)).Decoration);
        Assert.Equal(decoration, Assert.Single((await f.As<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId))).Items).Decoration);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        var trashed = await f.As<OrganizationChange>(f.Writer, new TrashPage(page.Id, new(false, tree.Version, 1)));
        await Denied(404, () => f.As<PageDecorationState>(f.Writer, new GetPageDecoration(page.Id)));
        await Denied(404, () => Save(f, page.Id, "star", "rose", decoration.RevisionId));
        await f.As<OrganizationChange>(f.Admin, new UntrashPage(page.Id, new(null, false, trashed.Version)));
        Assert.Equal(decoration, (await f.As<PageDecorationState>(f.Writer, new GetPageDecoration(page.Id))).Decoration);
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", f.Reader.Id);
        await Denied(404, () => f.As<PageDecorationState>(f.Reader, new GetPageDecoration(page.Id)));
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", f.Admin.Id);
        await Denied(401, () => f.As<PageDecorationState>(f.Admin, new GetPageDecoration(page.Id)));
    }

    [PostgreSqlFact]
    public async Task Tree_loads_all_visible_decorations_without_per_page_queries()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var first = await f.Create("First");
        await Save(f, first.Id, "folder", "slate");
        // The SQL of this test's own calls (the session factory's statistics count every test's on the shared host).
        int onePageStatements;
        using (var one = SqlRecording.Start())
        {
            Assert.Single((await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId))).Items);
            onePageStatements = one.Statements.Count;
        }
        foreach (var i in Enumerable.Range(0, 12))
        {
            // Each child inherits the decoration of its parent on creation.
            await f.Create("Child " + i, first.Id);
        }
        var plain = await f.Create("Plain");
        PageTreeDto tree;
        using (var many = SqlRecording.Start())
        {
            tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
            Assert.Equal(onePageStatements, many.Statements.Count);
        }
        Assert.Equal(14, tree.Items.Count);
        Assert.Equal(13, tree.Items.Count(item => item.Decoration is not null));
        Assert.Null(tree.Items.Single(item => item.Id == plain.Id).Decoration);
    }

    [PostgreSqlFact]
    public async Task Reader_takes_more_page_ids_than_one_statement_has_parameters()
    {
        // PostgreSQL's wire protocol carries at most 65,535 parameters per statement; a space with more listed pages must still load its tree.
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var decorated = await f.Create("Decorated");
        var decoration = (await Save(f, decorated.Id, "folder", "blue")).Decoration!;
        var plain = await f.Create("Plain");
        var ids = Enumerable.Range(0, 65_536).Select(_ => Guid.NewGuid()).Append(decorated.Id).Append(plain.Id).ToArray();
        using var session = f.Host.OpenSession();

        var read = await new PageDecorationReader(session).ReadManyAsync(ids, default);

        var (pageId, found) = Assert.Single(read);
        Assert.Equal(decorated.Id, pageId);
        Assert.Equal(decoration, found);
    }

    [PostgreSqlFact]
    public async Task Tree_statement_text_does_not_depend_on_the_number_of_pages()
    {
        // The tree sends one statement shape however many pages it lists, not a text that grows with the page count.
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var first = await f.Create("First");
        await Save(f, first.Id, "folder", "slate");
        async Task<(int Items, IReadOnlyList<string> Statements)> Tree()
        {
            using var recording = SqlRecording.Start();
            var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
            return (tree.Items.Count, recording.Statements);
        }
        await Tree();
        var onePage = await Tree();
        await f.Create("Second", first.Id);
        await f.Create("Third", first.Id);
        var threePages = await Tree();

        Assert.Equal(1, onePage.Items);
        Assert.Equal(3, threePages.Items);
        Assert.Equal(onePage.Statements, threePages.Statements);
    }

    private static Task<PageDecorationState> Save(PersonalLibraryFixture f, Guid pageId, string? icon, string? color, Guid? expected = null) =>
        f.As<PageDecorationState>(f.Writer, new SetPageDecoration(pageId, new(icon, color, expected)));

    private static async Task Denied(int status, Func<Task> action) =>
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
}
