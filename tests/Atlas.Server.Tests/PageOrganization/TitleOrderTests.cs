using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.PageOrganization;

/// <summary>„Řadit abecedně“ (3.6): a tree level pinned to its titles (TitleOrder), how it stays pinned and how a hand move ends it.</summary>
public sealed class TitleOrderTests
{
    /// <summary>Czech, in .NET and not by the database's collation: ch after h, č after c, diacritics, numbers by value, any case.</summary>
    [Fact]
    public void Titles_sort_in_czech()
    {
        string[] titles = ["Chata", "Hrad", "Ďábel", "Čaj", "cibule", "10. díl", "**Ahoj**", "2. díl"];

        Assert.Equal(["2. díl", "10. díl", "**Ahoj**", "cibule", "Čaj", "Ďábel", "Hrad", "Chata"], TitleOrder.ByTitle(titles, title => title));
        // A node without a title (a reference the reader may not read) goes last; equal titles keep their sibling order.
        Assert.Equal(["a", "B", "b", null], TitleOrder.ByTitle(new string?[] { null, "B", "a", "b" }, title => title));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task A_pinned_level_stays_sorted_for_new_renamed_and_moved_in_pages()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var hub = await f.CreateAsync("Rozcestník");
        var zeta = await f.CreateAsync("Zeta", hub);
        var alfa = await f.CreateAsync("Alfa", hub);
        var gama = await f.CreateAsync("Gama");
        Assert.Equal([zeta, alfa], await LevelAsync(f, hub));

        var pinned = await f.As<OrganizationChange>(f.Writer, new SortTreeLevel(f.SpaceId, new SortTreeLevelRequest(hub, true)));
        Assert.Equal([alfa, zeta], await LevelAsync(f, hub));
        Assert.True((await TreeAsync(f)).Items.Single(item => item.Id == hub).ChildrenByTitle);

        var beta = await f.CreateAsync("Beta", hub);
        Assert.Equal([alfa, beta, zeta], await LevelAsync(f, hub));
        await f.As(f.Writer, new RenamePage(zeta, new RenamePageRequest("Aaa", 1)));
        Assert.Equal([zeta, alfa, beta], await LevelAsync(f, hub));

        // From elsewhere into the pinned level: the pin stays and the page sorts in, whatever place it was dropped at.
        var movedIn = await f.As<OrganizationChange>(f.Writer, new MovePage(gama, new MovePageRequest(hub, zeta, (await TreeAsync(f)).Version)));
        Assert.False(movedIn.TitleOrderCancelled);
        Assert.True(movedIn.Version > pinned.Version);
        Assert.Equal([zeta, alfa, beta, gama], await LevelAsync(f, hub));
        // The numbers agree with the list for every client that sorts by them.
        var level = (await TreeAsync(f)).Items.Where(item => item.ParentId == hub).Select(item => item.SortOrder).ToArray();
        Assert.Equal(level.Order(), level);
    }

    [PostgreSqlFact]
    public async Task A_hand_move_within_the_level_ends_the_pin_and_undo_pins_it_again()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var c = await f.CreateAsync("Cyklus");
        var a = await f.CreateAsync("Agenda");
        var b = await f.CreateAsync("Bod");
        await f.As<OrganizationChange>(f.Writer, new SortTreeLevel(f.SpaceId, new SortTreeLevelRequest(null, true)));
        var tree = await TreeAsync(f);
        Assert.True(tree.RootByTitle);
        Assert.Equal([a, b, c], await LevelAsync(f, null));

        // Bod before Agenda by hand: the level keeps what it showed (not the order from before the pin, Cyklus first), the move applies
        // to it, and the pin is gone.
        var moved = await f.As<OrganizationChange>(f.Writer, new MovePage(b, new MovePageRequest(null, a, tree.Version)));
        Assert.True(moved.TitleOrderCancelled);
        Assert.False((await TreeAsync(f)).RootByTitle);
        Assert.Equal([b, a, c], await LevelAsync(f, null));

        // Zpět: pinned again, the order is the titles' again.
        await f.As<OrganizationChange>(f.Writer, new SortTreeLevel(f.SpaceId, new SortTreeLevelRequest(null, true)));
        Assert.Equal([a, b, c], await LevelAsync(f, null));

        // Unticking keeps the order the level showed, not the stored one from before the pin (Bod first); the same value again changes nothing.
        await f.As<OrganizationChange>(f.Writer, new SortTreeLevel(f.SpaceId, new SortTreeLevelRequest(null, false)));
        Assert.Equal([a, b, c], await LevelAsync(f, null));
        var same = await TreeAsync(f);
        Assert.Equal(same.Version, (await f.As<OrganizationChange>(f.Writer, new SortTreeLevel(f.SpaceId, new SortTreeLevelRequest(null, false)))).Version);
    }

    [PostgreSqlFact]
    public async Task Only_writers_pin_a_level_and_readers_see_it_sorted()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var hub = await f.CreateAsync("Rozcestník");
        var z = await f.CreateAsync("Zeta", hub);
        var a = await f.CreateAsync("Alfa", hub);
        foreach (var page in new[] { hub, z, a })
        {
            await f.PublishAsync(page);
        }

        // The root needs Writer on the space (403: the reader knows the space), a page's children EditContent on it (403 too).
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<OrganizationChange>(f.Reader, new SortTreeLevel(f.SpaceId, new SortTreeLevelRequest(null, true))))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<OrganizationChange>(f.Reader, new SortTreeLevel(f.SpaceId, new SortTreeLevelRequest(hub, true))))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<OrganizationChange>(f.Writer, new SortTreeLevel(Guid.NewGuid(), new SortTreeLevelRequest(hub, true))))).Status);

        await f.As<OrganizationChange>(f.Writer, new SortTreeLevel(f.SpaceId, new SortTreeLevelRequest(hub, true)));
        var reader = await f.As<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId));
        Assert.Equal([a, z], reader.Items.Where(item => item.ParentId == hub).Select(item => item.Id));
    }

    /// <summary>Migration 2026111033 over an existing tree: no level is pinned and every sibling order stays.</summary>
    [PostgreSqlFact]
    public async Task The_migration_pins_no_existing_level()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_tree_title_order_migration_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026111033);
        const string Owner = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a";
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Space','book-open','blue');
            INSERT INTO "SpaceOrganization"("SpaceId","Version") VALUES('{Space}',7);
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000a1','{Space}','{Owner}',1,2048),('00000000-0000-0000-0000-0000000000a2','{Space}','{Owner}',1,1024);
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("a1:2048:f|a2:1024:f|root:f:7", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(right(CAST("Id" AS text), 2) || ':' || "SortOrder" || ':' || CASE WHEN "SortChildrenByTitle" THEN 't' ELSE 'f' END, '|' ORDER BY "Id")
              || '|root:' || (SELECT CASE WHEN "SortRootByTitle" THEN 't' ELSE 'f' END || ':' || "Version" FROM "SpaceOrganization")
            FROM "Page"
            """));
    }

    private static Task<PageTreeDto> TreeAsync(PageOrganizationFixture f) => f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));

    private static async Task<Guid[]> LevelAsync(PageOrganizationFixture f, Guid? parentId)
        => (await TreeAsync(f)).Items.Where(item => item.ParentId == parentId).Select(item => item.Id).ToArray();

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}
