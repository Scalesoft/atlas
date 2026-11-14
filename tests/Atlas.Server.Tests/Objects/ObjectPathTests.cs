using System.Text.Json.Nodes;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Objects;

/// <summary>
/// The one write and read path of objects (F1 plan §2.3), proven by a test-only Fields type: create in a scope, read by who the
/// scope lets read, changes idempotent by change id and guarded by the expected version, typed refusals, and the library
/// filtered by the reader. Pages and boards are read through it but keep their own change paths.
/// </summary>
public sealed class ObjectPathTests
{
    private static Task<BoardsFixture> FixtureAsync() => BoardsFixture.CreateAsync(services => services.AddScoped<IObjectType, TestFieldsType>(), sharedAs: "boards-fields");

    [PostgreSqlFact]
    public async Task A_change_is_applied_once_per_change_id_checked_against_the_expected_version_and_refused_with_typed_errors()
    {
        await using var f = await FixtureAsync();
        var created = await f.As<ObjectDto>(f.Writer, Create(ObjectScopes.Space, f.SpaceId, """{"name":"Jana","email":"jana@example.com"}"""));
        Assert.Equal((TestFieldsType.Id, ObjectScopes.Space, (Guid?)f.SpaceId, 1L, "fields", true, "Jana"),
            (created.TypeId, created.Scope, created.SpaceId, created.Version, created.EditingModel, created.CanEdit, created.Title));
        Assert.Equal(f.Writer.Id, created.CreatedBy);

        var change = Guid.NewGuid();
        var payload = Fields("""{"email":"jana@atlas.test"}""");
        Assert.Equal(new ObjectChangeResult(2, false), await f.As<ObjectChangeResult>(f.Writer, new ApplyObjectChange(created.Id, change, 1, payload)));
        // A retry of the same change answers the original version, whatever version it expected; another change under that id is refused.
        Assert.Equal(new ObjectChangeResult(2, true), await f.As<ObjectChangeResult>(f.Writer, new ApplyObjectChange(created.Id, change, 1, Fields("""{"email":"jana@atlas.test"}"""))));
        await BoardsFixture.Refused(409, () => f.As(f.Writer, new ApplyObjectChange(created.Id, change, 2, Fields("""{"email":"other@atlas.test"}"""))), "operation_conflict");
        await BoardsFixture.Refused(409, () => f.As(f.Writer, new ApplyObjectChange(created.Id, Guid.NewGuid(), 1, Fields("""{"name":"Jane"}"""))), "version_conflict");
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new ApplyObjectChange(created.Id, Guid.NewGuid(), null, Fields("""{"name":" "}"""))), "invalid_object_change");
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new ApplyObjectChange(created.Id, null, null, Fields("""{"name":"X"}"""))), "invalid_object_change");
        // Without an expected version the change applies to whatever is current (last write wins per field).
        Assert.Equal(new ObjectChangeResult(3, false), await f.As<ObjectChangeResult>(f.Writer, new ApplyObjectChange(created.Id, Guid.NewGuid(), null, Fields("""{"name":"Jane"}"""))));

        var read = await f.As<ObjectDto>(f.Reader, new GetObject(created.Id));
        Assert.Equal((3L, false, "Jane"), (read.Version, read.CanEdit, read.Title));
        Assert.Equal("""{"name":"Jane","email":"jana@atlas.test"}""", read.Data!.ToJsonString());
        await BoardsFixture.Refused(403, () => f.As(f.Reader, new ApplyObjectChange(created.Id, Guid.NewGuid(), null, Fields("""{"name":"R"}"""))));
        await BoardsFixture.Refused(404, () => f.As(f.Outsider, new GetObject(created.Id)));
        await BoardsFixture.Refused(404, () => f.As(f.Outsider, new ApplyObjectChange(created.Id, Guid.NewGuid(), null, Fields("""{"name":"O"}"""))));
        using var session = f.Host.OpenSession();
        // The creation and the two applied changes; refusals and the retry wrote nothing.
        Assert.Equal(3L, await session.CreateSQLQuery("""SELECT count(*) FROM "ObjectChange" WHERE "ObjectId"=:id""").SetParameter("id", created.Id).UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Pages_and_boards_are_read_through_the_object_path_but_changed_only_on_their_own_paths()
    {
        await using var f = await FixtureAsync();
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Runbook", IndexFixture.Rich("Steps"), "x"), f.Writer, default);
        await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        var board = await f.CreateBoardAsync(title: "Wall");

        var readPage = await f.As<ObjectDto>(f.Reader, new GetObject(page.Id));
        Assert.Equal(("atlas.page", "yjsText", "Runbook", false, (JsonObject?)null), (readPage.TypeId, readPage.EditingModel, readPage.Title, readPage.CanEdit, readPage.Data));
        var readBoard = await f.As<ObjectDto>(f.Writer, new GetObject(board));
        Assert.Equal(("atlas.board", "boardOps", "Wall", true, 0L), (readBoard.TypeId, readBoard.EditingModel, readBoard.Title, readBoard.CanEdit, readBoard.Version));

        await BoardsFixture.Refused(400, () => f.As(f.Writer, new ApplyObjectChange(page.Id, Guid.NewGuid(), null, Fields("""{"name":"x"}"""))), "unsupported_change");
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new ApplyObjectChange(board, Guid.NewGuid(), null, Fields("""{"name":"x"}"""))), "unsupported_change");
        await BoardsFixture.Refused(400, () => f.As(f.Writer, Create(ObjectScopes.Space, f.SpaceId, "{}", "atlas.page")), "unsupported_change");
        await BoardsFixture.Refused(400, () => f.As(f.Writer, Create(ObjectScopes.Space, f.SpaceId, "{}", "atlas.unknown")), "unknown_type");
    }

    /// <summary>The scope decides who reads and writes: the wiki is read by everyone and changed by a WikiAdmin, a private object
    /// is its owner's, a space object follows the space role; the scope's shape is validated before anything is written.</summary>
    [PostgreSqlFact]
    public async Task The_scope_decides_who_may_create_read_and_change_an_object()
    {
        await using var f = await FixtureAsync();
        await BoardsFixture.Refused(403, () => f.As(f.Writer, Create(ObjectScopes.Wiki, null, """{"name":"Directory"}""")));
        var wiki = await f.As<ObjectDto>(f.Admin, Create(ObjectScopes.Wiki, null, """{"name":"Directory"}"""));
        Assert.False((await f.As<ObjectDto>(f.Outsider, new GetObject(wiki.Id))).CanEdit);
        await BoardsFixture.Refused(403, () => f.As(f.Outsider, new ApplyObjectChange(wiki.Id, Guid.NewGuid(), null, Fields("""{"name":"Mine"}"""))));

        var mine = await f.As<ObjectDto>(f.Outsider, Create(ObjectScopes.Private, null, """{"name":"Note to self"}"""));
        Assert.Equal(f.Outsider.Id, mine.OwnerId);
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new GetObject(mine.Id)));

        await BoardsFixture.Refused(403, () => f.As(f.Reader, Create(ObjectScopes.Space, f.SpaceId, """{"name":"No"}""")));
        await BoardsFixture.Refused(404, () => f.As(f.Outsider, Create(ObjectScopes.Space, f.SpaceId, """{"name":"No"}""")));
        foreach (var (scope, space) in new[] { (ObjectScopes.Space, (Guid?)null), (ObjectScopes.Wiki, f.SpaceId), (ObjectScopes.Private, f.SpaceId), ("team", null) })
        {
            await BoardsFixture.Refused(400, () => f.As(f.Admin, Create(scope, space, """{"name":"Bad"}""")), "invalid_object");
        }
        await BoardsFixture.Refused(400, () => f.As(f.Writer, Create(ObjectScopes.Space, f.SpaceId, """{"title":"No name"}""")), "invalid_object_change");
    }

    /// <summary>The library lists what the reader may read and nothing else, not even in its total: other people's private objects,
    /// spaces it has no role in, trashed objects and objects of types no enabled module registers stay out.</summary>
    [PostgreSqlFact]
    public async Task The_library_lists_only_what_the_reader_may_read_with_type_scope_space_text_and_paging()
    {
        await using var f = await FixtureAsync();
        // Every name ends in this test's token: the wiki objects and other spaces of the shared host's tests stay out of the lists.
        var tag = Guid.NewGuid().ToString("N")[..8];
        await f.As<ObjectDto>(f.Writer, Create(ObjectScopes.Space, f.SpaceId, $$"""{"name":"Alpha contact {{tag}}"}"""));
        await f.As<ObjectDto>(f.Admin, Create(ObjectScopes.Space, f.OtherSpaceId, $$"""{"name":"Beta contact {{tag}}"}"""));
        await f.As<ObjectDto>(f.Admin, Create(ObjectScopes.Wiki, null, $$"""{"name":"Gamma 100%_{{tag}}"}"""));
        await f.As<ObjectDto>(f.Reader, Create(ObjectScopes.Private, null, $$"""{"name":"Delta {{tag}}"}"""));
        var trashed = await f.As<ObjectDto>(f.Writer, Create(ObjectScopes.Space, f.SpaceId, $$"""{"name":"Epsilon {{tag}}"}"""));
        await f.Host.ExecuteAsync("""UPDATE "ContentResource" SET "TrashedAt"=now() WHERE "Id"=:id""", trashed.Id);
        // An object of a type no enabled module registers (a module switched off) is unknown: not listed, not readable.
        await f.Host.ExecuteAsync("""UPDATE "ContentResource" SET "TypeId"='gone.type' WHERE "Id"=:id""",
            (await f.As<ObjectDto>(f.Admin, Create(ObjectScopes.Wiki, null, $$"""{"name":"Orphan {{tag}}"}"""))).Id);

        // Titles (without the token) in the list's order and the total; the text filter is the token unless a test names another.
        async Task<string> ListAsync(UserAccount actor, string? type = null, string? scope = null, Guid? spaceId = null, string? q = null, int page = 1)
        {
            var list = await f.As<ObjectList>(actor, new ListObjects(type, scope, spaceId, q ?? tag, page));
            return string.Join(", ", list.Items.Select(item => item.Title.Replace(tag, "").TrimEnd())) + " | " + list.Total;
        }

        Assert.Equal("Delta, Gamma 100%_, Alpha contact | 3", await ListAsync(f.Reader));
        Assert.Equal("Gamma 100%_ | 1", await ListAsync(f.Outsider));
        Assert.Equal("Gamma 100%_, Beta contact, Alpha contact | 3", await ListAsync(f.Admin));
        Assert.Equal("Alpha contact | 1", await ListAsync(f.Reader, scope: ObjectScopes.Space, spaceId: f.SpaceId));
        Assert.Equal(" | 0", await ListAsync(f.Reader, spaceId: f.OtherSpaceId));
        Assert.Equal("Alpha contact | 1", await ListAsync(f.Reader, q: "alpha contact " + tag));
        // Unescaped, "%_" would match every title with a character before the token.
        Assert.Equal("Gamma 100%_ | 1", await ListAsync(f.Reader, q: "%_" + tag));
        Assert.Equal("Delta, Gamma 100%_, Alpha contact | 3", await ListAsync(f.Reader, type: TestFieldsType.Id));
        Assert.Equal(" | 0", await ListAsync(f.Reader, type: "atlas.page"));
        Assert.Equal(" | 3", await ListAsync(f.Reader, page: 2));
        // A batch of exact names answers with the same access rules: Delta is the reader's own, Beta lives in a space it has no role in.
        async Task<string> NamedAsync(UserAccount actor, params string[] names)
            => string.Join(", ", (await f.As<ObjectList>(actor, new ListObjects(null, null, null, null, 1, names))).Items.Select(item => item.Title.Replace(tag, "").TrimEnd()));
        Assert.Equal("Delta, Alpha contact", await NamedAsync(f.Reader, $" ALPHA contact {tag}", $"delta {tag}", $"Beta contact {tag}", "Alpha"));
        Assert.Equal("", await NamedAsync(f.Outsider, $"Delta {tag}", $"Alpha contact {tag}"));
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new GetObject(trashed.Id)));
    }

    private static CreateObject Create(string scope, Guid? spaceId, string data, string typeId = TestFieldsType.Id)
    {
        return new CreateObject(typeId, scope, spaceId, JsonNode.Parse(data)!.AsObject());
    }

    private static JsonObject Fields(string fields) => new() { ["fields"] = JsonNode.Parse(fields) };
}
