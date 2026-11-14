using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.AccessAudit.Queries;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.Spaces.Commands;
using Atlas.Server.Features.Spaces.Contracts;
using Atlas.Server.Features.Spaces.Queries;
using Atlas.Server.Tests.Boards;
using Xunit;

namespace Atlas.Server.Tests.Spaces;

/// <summary>
/// The permanent delete of pages, boards of a space and spaces (unified trash spec §2, §3; ARCH1-1): who may (space Admin for pages and
/// boards, wiki admin only for spaces, by the typed name), what goes (content, revisions' text, projections, comments, favorites, boards
/// under it; grants closed, never deleted), what stays (a tombstone the access history names, a subpage of another batch, restorable
/// elsewhere) and the one audit row each the Security centre lists (SEC1-4).
/// </summary>
public sealed class PermanentDeleteTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"tajné"}]}]}}""";

    [PostgreSqlFact]
    public async Task A_space_admin_purges_a_page_with_its_batch_and_nothing_of_it_is_left_but_its_tombstone()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var spaceAdmin = await f.Host.SeedUserAsync("spaceadmin");
        var space = await f.Host.SeedSpaceAsync("Team", (spaceAdmin, SpaceRole.Admin), (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
        var page = await CreatePageAsync(f, space, null, "Plán");
        var child = await CreatePageAsync(f, space, page, "Podstránka");
        var earlier = await CreatePageAsync(f, space, page, "Dřív v koši");
        var board = (await f.As<Features.Boards.Contracts.BoardDto>(f.Writer, new CreateBoard(space, "Tabule", child))).Id;
        await f.As(f.Writer, new PostComment(page, "Komentář", null));
        await f.As<PersonalPageState>(f.Reader, new SetPageFavorite(page, new SetFavorite(true)));
        await f.As(f.Writer, new TrashPage(earlier, new TrashPageRequest(false, await TreeVersionAsync(f, space), await VersionAsync(f, earlier))));
        await f.As(f.Writer, new TrashPage(page, new TrashPageRequest(true, await TreeVersionAsync(f, space), await VersionAsync(f, page))));

        // Rights: only who manages the space's trash; anyone else learns nothing more than the trash list tells them.
        foreach (var refused in new[] { f.Writer, f.Reader, f.Outsider })
        {
            Assert.InRange((await Assert.ThrowsAsync<ApiException>(() => f.As(refused, new PurgeObject(page)))).Status, 403, 404);
        }
        await f.As(spaceAdmin, new PurgeObject(page));

        // The batch is gone from the trash; the subpage trashed earlier stays and comes back elsewhere.
        var trash = await f.As<PageTrashDto>(spaceAdmin, new ListPageTrash(space, 0, 50));
        Assert.Equal([earlier], trash.Items.Select(x => x.Id));
        Assert.Equal("writer", trash.Items[0].TrashedByName);
        await BoardsFixture.Refused(404, () => f.As(spaceAdmin, new PurgeObject(page)));
        await BoardsFixture.Refused(404, () => f.As(spaceAdmin, new RestoreObject(child)));
        await BoardsFixture.Refused(404, () => f.As(spaceAdmin, new UntrashPage(page, new UntrashPageRequest(null, true, trash.Version))));
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new GetPage(page)));
        await f.As<OrganizationChange>(spaceAdmin, new UntrashPage(earlier, new UntrashPageRequest(null, false, trash.Version)));

        using var session = f.Host.OpenSession();
        async Task<long> CountAsync(string sql) => await session.CreateSQLQuery(sql.Replace(":page", $"'{page}'").Replace(":child", $"'{child}'")
            .Replace(":board", $"'{board}'")).UniqueResultAsync<long>();
        Assert.Equal(2L, await CountAsync("""SELECT count(*) FROM "Page" WHERE "Id" IN (:page, :child) AND "PurgedAt" IS NOT NULL AND "IsTrashed" """));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "PageRevision" WHERE "PageId" IN (:page, :child) AND ("Title" <> '' OR "Markdown" <> '' OR "ContentJson" LIKE '%tajné%')"""));
        foreach (var table in new[] { "SearchDocument", "WorkingSearchDocument", "PageContribution", "PageFavorite", "PageVisit", "IndexTask" })
        {
            Assert.Equal(0L, await CountAsync($"""SELECT count(*) FROM "{table}" WHERE "PageId" IN (:page, :child)"""));
        }
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "Comment" WHERE "ObjectId" IN (:page, :child)"""));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "Board" WHERE "Id" = :board"""));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = :board"""));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "PageId" IN (:page, :child) AND "ActiveTo" IS NULL"""));

        var audit = await f.As<AccessAuditList>(f.Admin, new GetAccessAudit(null, 50, SpaceId: space));
        var row = Assert.Single(audit.Items, x => x.Action == "page.purged");
        Assert.Equal((spaceAdmin.Id, space, "atlas.page", "Plán"), (row.ActorId!.Value, row.SpaceId!.Value, row.ObjectTypeId, row.ObjectTitle));
    }

    [PostgreSqlFact]
    public async Task A_board_of_a_space_is_restored_and_purged_through_the_object_path_by_the_space_admin()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var spaceAdmin = await f.Host.SeedUserAsync("spaceadmin");
        var space = await f.Host.SeedSpaceAsync("Team", (spaceAdmin, SpaceRole.Admin), (f.Writer, SpaceRole.Writer));
        var kept = await f.CreateBoardAsync(f.Writer, space, "Zůstane");
        var gone = await f.CreateBoardAsync(f.Writer, space, "Zmizí");
        await f.As(f.Writer, new DeleteBoard(kept));
        await f.As(f.Writer, new DeleteBoard(gone));

        await BoardsFixture.Refused(403, () => f.As(f.Writer, new PurgeObject(gone)));
        await BoardsFixture.Refused(404, () => f.As(f.Outsider, new PurgeObject(gone)));
        await f.As<Features.Objects.Contracts.ObjectChangeResult>(spaceAdmin, new RestoreObject(kept));
        await f.As(spaceAdmin, new PurgeObject(gone));
        await BoardsFixture.Refused(404, () => f.As(spaceAdmin, new PurgeObject(gone)));

        Assert.Equal(kept, (await f.As<Features.Boards.Contracts.BoardDto>(f.Writer, new Features.Boards.Queries.GetBoard(kept))).Id);
        Assert.Empty((await f.As<PageTrashDto>(spaceAdmin, new ListPageTrash(space, 0, 50))).Items);
        using var session = f.Host.OpenSession();
        Assert.Equal(0L, await session.CreateSQLQuery("""SELECT count(*) FROM "ContentResource" WHERE "Id" = :id""").SetParameter("id", gone).UniqueResultAsync<long>());
        var row = Assert.Single((await f.As<AccessAuditList>(f.Admin, new GetAccessAudit(null, 50, SpaceId: space))).Items, x => x.Action == "object.purged");
        Assert.Equal((space, "atlas.board", "Zmizí"), (row.SpaceId!.Value, row.ObjectTypeId, row.ObjectTitle));
    }

    [PostgreSqlFact]
    public async Task Only_a_wiki_admin_purges_a_trashed_space_by_its_name_and_it_never_comes_back()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var spaceAdmin = await f.Host.SeedUserAsync("spaceadmin");
        var space = await f.Host.SeedSpaceAsync("Provoz", (spaceAdmin, SpaceRole.Admin), (f.Writer, SpaceRole.Writer));
        var page = await CreatePageAsync(f, space, null, "Runbook");
        var board = await f.CreateBoardAsync(f.Writer, space, "Plán");
        // The location's name is unique in the wiki (UX_VaultLocation_Name): one of this test's own.
        var location = Guid.NewGuid();
        await f.Host.ExecuteAsync($"""
            INSERT INTO "VaultLocation"("Id","Name","Kind","Root") VALUES ('{location}','Spravované {location:N}','managed','');
            INSERT INTO "Vault"("Id","SpaceId","Name","LocationId","FileName","State","CreatedBy","CreatedAt")
              VALUES (gen_random_uuid(), :id, 'Servery', '{location}', 'servery.kdbx', 'locked', '{f.Admin.Id}', now());
            """, space);
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new PurgeSpace(space, new PurgeSpaceRequest("Provoz"))));
        await f.As(f.Admin, new TrashSpace(space, new TrashSpaceRequest("Provoz")));
        Assert.Equal(["Servery"], Assert.Single(await f.As<IReadOnlyList<TrashedSpaceItem>>(f.Admin, new ListSpaceTrash()), x => x.Id == space).VaultNames);

        await BoardsFixture.Refused(403, () => f.As(spaceAdmin, new PurgeSpace(space, new PurgeSpaceRequest("Provoz"))));
        await BoardsFixture.Refused(400, () => f.As(f.Admin, new PurgeSpace(space, new PurgeSpaceRequest("provoz"))), "space_name_mismatch");
        await f.As(f.Admin, new PurgeSpace(space, new PurgeSpaceRequest(" Provoz ")));

        Assert.DoesNotContain(await f.As<IReadOnlyList<TrashedSpaceItem>>(f.Admin, new ListSpaceTrash()), x => x.Id == space);
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new RestoreSpace(space)));
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new PurgeSpace(space, new PurgeSpaceRequest("Provoz"))));
        using var session = f.Host.OpenSession();
        async Task<long> CountAsync(string sql) => await session.CreateSQLQuery(sql).SetParameter("space", space).UniqueResultAsync<long>();
        Assert.Equal(1L, await CountAsync("""SELECT count(*) FROM "Space" WHERE "Id" = :space AND "PurgedAt" IS NOT NULL"""));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "Page" WHERE "SpaceId" = :space AND "PurgedAt" IS NULL"""));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "Board" WHERE "SpaceId" = :space"""));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "SpaceGrant" WHERE "SpaceId" = :space AND "ActiveTo" IS NULL"""));
        Assert.Equal(2L, await CountAsync("""SELECT count(*) FROM "SpaceGrant" WHERE "SpaceId" = :space"""));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "Vault" WHERE "SpaceId" = :space AND ("ArchivedAt" IS NULL OR "ProtectedKdbxPassword" IS NOT NULL)"""));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId" WHERE p."SpaceId" = :space AND r."Title" <> '' """));
        Assert.Equal(1L, await session.CreateSQLQuery("""SELECT count(*) FROM "Page" WHERE "Id" = :id AND "PurgedAt" IS NOT NULL""").SetParameter("id", page).UniqueResultAsync<long>());
        Assert.Equal(0L, await session.CreateSQLQuery("""SELECT count(*) FROM "Board" WHERE "Id" = :id""").SetParameter("id", board).UniqueResultAsync<long>());

        var audit = (await f.As<AccessAuditList>(f.Admin, new GetAccessAudit(null, 100, SpaceId: space))).Items;
        var row = Assert.Single(audit, x => x.Action == "space.purged");
        Assert.Equal((f.Admin.Id, space, "Provoz"), (row.ActorId!.Value, row.SpaceId!.Value, row.ObjectTitle));
        // Its grants ended with it, in the same history.
        Assert.Equal(2, audit.Count(x => x.Action == "grant.removed" && x.SpaceId == space));
    }

    private static async Task<Guid> CreatePageAsync(BoardsFixture f, Guid space, Guid? parent, string title)
    {
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(space, parent, title, Rich, "tajné")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        return page.Id;
    }

    private static async Task<int> VersionAsync(BoardsFixture f, Guid page) => (await f.As<PageDto>(f.Writer, new GetPage(page))).Version;

    private static async Task<long> TreeVersionAsync(BoardsFixture f, Guid space)
    {
        using var session = f.Host.OpenSession();
        return await session.CreateSQLQuery("""SELECT "Version" FROM "SpaceOrganization" WHERE "SpaceId" = :id""").SetParameter("id", space).UniqueResultAsync<long>();
    }
}
