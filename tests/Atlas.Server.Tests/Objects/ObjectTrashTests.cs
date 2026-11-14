using System.Text.Json.Nodes;
using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Xunit;

namespace Atlas.Server.Tests.Objects;

/// <summary>
/// The trash of the objects the platform stores (unified trash spec §2): who sees it follows the scope (who writes the type's wiki objects,
/// the space's Admin, the private owner), a restore brings the object back as it was with a new version, a purge leaves only its audit.
/// </summary>
public sealed class ObjectTrashTests
{
    [PostgreSqlFact]
    public async Task The_trash_lists_only_what_the_reader_may_restore_with_who_trashed_it()
    {
        await using var f = await BoardsFixture.SharedAsync();
        // The writer administers this space; the reader writes in it, and so writes the wiki directory too.
        var team = await f.Host.SeedSpaceAsync("Team", (f.Writer, SpaceRole.Admin), (f.Reader, SpaceRole.Writer));
        var jana = await TrashedAsync(f, f.Writer, Create("Jana", ObjectScopes.Wiki));
        var mine = await TrashedAsync(f, f.Outsider, Create("Mine", ObjectScopes.Private));
        var teams = await TrashedAsync(f, f.Reader, Create("Team's", ObjectScopes.Space, team));
        var live = (await f.As<ObjectDto>(f.Writer, Create("Live", ObjectScopes.Wiki))).Id;
        // The wiki trash and the spaces of the shared host's other tests stay out: only this test's objects are compared.
        Guid[] own = [jana, mine, teams, live];

        async Task<string> TrashOf(UserAccount actor, string? scope = null) =>
            string.Join(", ", (await f.As<ObjectTrashList>(actor, new ListObjectTrash(ContactObjectType.Id, scope, null))).Items.Where(item => own.Contains(item.Id)).Select(item => item.Title).Order());
        Assert.Equal("Jana, Team's", await TrashOf(f.Writer));
        Assert.Equal("Jana", await TrashOf(f.Reader));
        Assert.Equal("Mine", await TrashOf(f.Outsider));
        Assert.Equal("Jana, Team's", await TrashOf(f.Admin));
        Assert.Equal("Team's", await TrashOf(f.Writer, ObjectScopes.Space));

        var row = (await f.As<ObjectTrashList>(f.Writer, new ListObjectTrash(null, ObjectScopes.Wiki, null))).Items.Where(item => own.Contains(item.Id)).Single();
        Assert.Equal((jana, f.Writer.Id, "writer", "Jana"), (row.Id, row.TrashedById, row.TrashedByName, row.Data!["name"]!.GetValue<string>()));
        Assert.NotEqual(default, row.TrashedAt);
        await BoardsFixture.Refused(404, () => f.As(f.Reader, new RestoreObject(teams)));
        await BoardsFixture.Refused(404, () => f.As(f.Writer, new PurgeObject(mine)));
    }

    [PostgreSqlFact]
    public async Task A_restore_brings_the_object_back_as_it_was_with_a_new_version()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var jana = await TrashedAsync(f, f.Writer, Create("Jana", ObjectScopes.Wiki));
        // In the trash it stays out of every write.
        await BoardsFixture.Refused(404, () => f.As(f.Writer, new ApplyObjectChange(jana, Guid.NewGuid(), null, new JsonObject { ["fields"] = new JsonObject { ["title"] = "x" } })));
        await BoardsFixture.Refused(404, () => f.As(f.Outsider, new RestoreObject(jana)));

        Assert.Equal(new ObjectChangeResult(2, false), await f.As<ObjectChangeResult>(f.Writer, new RestoreObject(jana)));
        var back = await f.As<ObjectDto>(f.Reader, new GetObject(jana));
        Assert.Equal(("Jana", 2L), (back.Title, back.Version));
        Assert.DoesNotContain((await f.As<ObjectTrashList>(f.Writer, new ListObjectTrash(null, null, null))).Items, item => item.Id == jana);
        Assert.Equal(3, (await f.As<ObjectChangeResult>(f.Writer, new ApplyObjectChange(jana, Guid.NewGuid(), 2, new JsonObject { ["fields"] = new JsonObject { ["title"] = "x" } }))).Version);
        await BoardsFixture.Refused(404, () => f.As(f.Writer, new RestoreObject(jana)));
        await BoardsFixture.Refused(404, () => f.As(f.Writer, new PurgeObject(jana)));
        using var session = f.Host.OpenSession();
        Assert.Equal(1L, await session.CreateSQLQuery("""SELECT count(*) FROM "ContentResource" WHERE "Id"=:id AND "TrashedAt" IS NULL AND "TrashedById" IS NULL""")
            .SetParameter("id", jana).UniqueResultAsync<long>());
        Assert.Equal("""{"restored": true}""", await session.CreateSQLQuery("""SELECT CAST("PayloadJson" AS text) FROM "ObjectChange" WHERE "ObjectId"=:id AND "Version"=2""")
            .SetParameter("id", jana).UniqueResultAsync<string>());
    }

    [PostgreSqlFact]
    public async Task A_purge_removes_the_object_its_history_and_comments_and_leaves_its_audit()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var jana = (await f.As<ObjectDto>(f.Writer, Create("Jana", ObjectScopes.Wiki))).Id;
        await f.As<ObjectChangeResult>(f.Writer, new ApplyObjectChange(jana, Guid.NewGuid(), null, new JsonObject { ["fields"] = new JsonObject { ["title"] = "Ředitelka" } }));
        await f.As<CommentDto>(f.Writer, new PostComment(jana, "Ozvat se @writer", null));
        var karel = (await f.As<ObjectDto>(f.Writer, Create("Karel", ObjectScopes.Wiki))).Id;
        await f.As(f.Writer, new TrashObject(jana));
        await BoardsFixture.Refused(404, () => f.As(f.Outsider, new PurgeObject(jana)));

        await f.As(f.Writer, new PurgeObject(jana));

        using var session = f.Host.OpenSession();
        Assert.Equal("0|0|0|1", await session.CreateSQLQuery("""
            SELECT (SELECT count(*) FROM "ContentResource" WHERE "Id"=:id) || '|' || (SELECT count(*) FROM "ObjectChange" WHERE "ObjectId"=:id) || '|' ||
              (SELECT count(*) FROM "Comment" WHERE "ObjectId"=:id) || '|' || (SELECT count(*) FROM "ContentResource" WHERE "Id"=:karel)
            """).SetParameter("id", jana).SetParameter("karel", karel).UniqueResultAsync<string>());
        var audit = await session.CreateSQLQuery("""SELECT "TypeId" || '|' || "Scope" || '|' || "Title" || '|' || CAST("PurgedById" AS text) FROM "ObjectPurgeEvent" WHERE "ObjectId"=:id""")
            .SetParameter("id", jana).UniqueResultAsync<string>();
        Assert.Equal($"atlas.contact|wiki|Jana|{f.Writer.Id}", audit);
        await BoardsFixture.Refused(404, () => f.As(f.Writer, new RestoreObject(jana)));
        await BoardsFixture.Refused(404, () => f.As(f.Reader, new GetObject(jana)));
    }

    private static async Task<Guid> TrashedAsync(BoardsFixture f, UserAccount actor, CreateObject create)
    {
        var id = (await f.As<ObjectDto>(actor, create)).Id;
        await f.As(actor, new TrashObject(id));
        return id;
    }

    private static CreateObject Create(string name, string scope, Guid? spaceId = null)
    {
        return new CreateObject(ContactObjectType.Id, scope, spaceId, new JsonObject { ["name"] = name });
    }
}
