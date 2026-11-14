using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Objects;

/// <summary>
/// Pages and boards as object types (F1 plan §2.2): registered by their modules, they decide access as their own paths do and
/// project their readable public form, the one text search and AI index once per object, with chunks anchored where a citation
/// points (a heading, a board item) and without secret references. Anything not registered is unknown: fail closed.
/// </summary>
public sealed class ObjectTypeTests
{
    [PostgreSqlFact]
    public async Task A_page_decides_access_as_the_page_paths_and_projects_only_its_published_revision()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Handbook", IndexFixture.Rich("Intro text\n## Setup\nInstall it", ("Setup", "setup")), "x"),
            f.Writer, default);

        // An unpublished draft is its owner's (and a WikiAdmin's), as the page paths decide; and no public form to index yet.
        Assert.Equal([ObjectAccess.Write, ObjectAccess.None, ObjectAccess.None, ObjectAccess.Write],
            await AccessAsync(f, "atlas.page", page.Id, f.Writer, f.Reader, f.Outsider, f.Admin));
        Assert.Null(await ProjectAsync(f, "atlas.page", page.Id));

        var published = await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        Assert.Equal([ObjectAccess.Write, ObjectAccess.Read, ObjectAccess.None, ObjectAccess.Write],
            await AccessAsync(f, "atlas.page", page.Id, f.Writer, f.Reader, f.Outsider, f.Admin));
        await f.Host.ExecuteAsync("""UPDATE "PageRevision" SET "Markdown"="Markdown"||' :secret{ref="vault/db"}' WHERE "Id"=(SELECT "PublishedRevisionId" FROM "Page" WHERE "Id"=:id)""", page.Id);
        await pages.SaveAsync(page.Id, new SavePageRequest("Handbook draft", IndexFixture.Rich("Unpublished"), null, published.Version), f.Writer, default);
        var projection = Assert.IsType<ObjectProjection>(await ProjectAsync(f, "atlas.page", page.Id));

        Assert.Equal("Handbook", projection.Title);
        Assert.DoesNotContain("Unpublished", projection.Markdown);
        Assert.DoesNotContain("secret", projection.Markdown + projection.PlainText + string.Concat(projection.Chunks.Select(chunk => chunk.Text)));
        Assert.DoesNotContain("#", projection.PlainText);
        Assert.Contains("Install it", projection.PlainText);
        Assert.Equal([(null, ""), ("setup", "Setup")], projection.Chunks.Select(chunk => (chunk.Anchor, chunk.Heading)));
        Assert.Contains("Install it", projection.Chunks[1].Text);
    }

    [PostgreSqlFact]
    public async Task A_board_decides_access_by_the_space_role_and_projects_its_items_as_chunks_anchored_at_them()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var board = await f.CreateBoardAsync(title: "Roadmap");
        var note = Guid.NewGuid();
        var gone = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, board, BoardsFixture.Note(note, "**Launch** in May :secret{ref=\"vault/key\"}"), BoardsFixture.Note(gone, "Dropped idea"));
        await f.ApplyAsync(f.Writer, board, new BoardChange("delete", ItemId: gone));

        Assert.Equal([ObjectAccess.Write, ObjectAccess.Read, ObjectAccess.None, ObjectAccess.Write],
            await AccessAsync(f, "atlas.board", board, f.Writer, f.Reader, f.Outsider, f.Admin));
        var projection = Assert.IsType<ObjectProjection>(await ProjectAsync(f, "atlas.board", board));
        Assert.Equal("Roadmap", projection.Title);
        var chunk = Assert.Single(projection.Chunks);
        Assert.Equal((note.ToString("D"), "Roadmap"), (chunk.Anchor, chunk.Heading));
        Assert.Equal("Launch in May", chunk.Text.Trim());
        Assert.Contains("**Launch**", projection.Markdown);
        Assert.DoesNotContain("secret", projection.Markdown);

        await f.As(f.Writer, new Atlas.Server.Features.Boards.Commands.DeleteBoard(board));
        Assert.Equal([ObjectAccess.None], await AccessAsync(f, "atlas.board", board, f.Writer));
        Assert.Null(await ProjectAsync(f, "atlas.board", board));
    }

    [PostgreSqlFact]
    public async Task Only_registered_types_and_views_are_known()
    {
        await using var f = await BoardsFixture.SharedAsync();
        using var scope = f.Host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<ObjectTypes>();
        Assert.Equal((ObjectEditingModel.YjsText, ObjectEditingModel.BoardOps), (types.Find("atlas.page")!.EditingModel, types.Find("atlas.board")!.EditingModel));
        Assert.Null(types.Find("atlas.unknown"));
        Assert.Equal(("atlas.page", "atlas.board"), (types.View("page")!.TypeId, types.View("board")!.TypeId));
        Assert.Null(types.View("doc"));
        Assert.Equal(new[] { "atlas.contact", "atlas.note" }, types.StoredTypeIds());
    }

    private static async Task<ObjectAccess[]> AccessAsync(BoardsFixture f, string typeId, Guid objectId, params UserAccount[] accounts)
    {
        var result = new List<ObjectAccess>();
        foreach (var account in accounts)
        {
            using var scope = f.Host.Services.CreateScope();
            var type = scope.ServiceProvider.GetRequiredService<ObjectTypes>().Find(typeId)!;
            result.Add(await type.AuthorizeAsync(new ObjectActor(account), objectId, default));
        }
        return result.ToArray();
    }

    private static async Task<ObjectProjection?> ProjectAsync(BoardsFixture f, string typeId, Guid objectId)
    {
        using var scope = f.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ObjectTypes>().Find(typeId)!.ProjectAsync(objectId, default);
    }
}
