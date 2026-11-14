using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>Object cards (objects spec §2.3, §5.1): a board shows a live card of an object its author may read; each reader sees the
/// object as they may (its data, whether they may change it) or a locked card, and the board's view session holds the object.</summary>
public sealed class BoardObjectCardTests
{
    [PostgreSqlFact]
    public async Task A_readable_object_is_placed_as_a_live_card_that_each_reader_sees_as_they_may()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var board = await f.CreateBoardAsync();
        // The directory holds one person per address across the wiki: the address is this test's own.
        var jana = await f.As<ObjectDto>(f.Writer, Contact(ObjectScopes.Wiki, $$"""{"name":"**Jana**","emails":["jana-{{Guid.NewGuid():N}}@example.com"]}"""));
        var secret = await f.As<ObjectDto>(f.Admin, Contact(ObjectScopes.Private, """{"name":"Admin's own"}"""));
        var card = Guid.NewGuid();

        await f.ApplyAsync(f.Writer, board, Place(card, jana.Id));
        // Placing an object grants nothing: the author must read it, and it must be of the type the card names.
        await BoardsFixture.Refused(400, () => f.ApplyAsync(f.Writer, board, Place(Guid.NewGuid(), secret.Id)), "invalid_board_change");
        await BoardsFixture.Refused(400, () => f.ApplyAsync(f.Writer, board, Place(Guid.NewGuid(), jana.Id, "atlas.page")), "invalid_board_change");

        var read = Assert.Single((await f.As<BoardDto>(f.Reader, new GetBoard(board))).Items, item => item.Id == card);
        Assert.Equal((ContactObjectType.Id, jana.Id, "Jana", true, false, 1L), (read.Target!.Type, read.Target.ResourceId, read.Target.Title, read.Target.Available,
            read.Target.CanEdit, read.Target.Version));
        Assert.Equal("**Jana**", read.Target.Data!["name"]!.GetValue<string>());
        Assert.True(Assert.Single((await f.As<BoardDto>(f.Writer, new GetBoard(board))).Items, item => item.Id == card).Target!.CanEdit);

        // The board's view session holds the object, so its changes reach the board.
        var members = await f.As<ViewMemberList>(f.Writer, new GetViewMembers("board", board));
        Assert.Contains(members.Members, member => member.ObjectId == jana.Id);

        // An object gone from the reader's reach is a locked card: its type, nothing of its content.
        await f.As(f.Writer, new TrashObject(jana.Id));
        var locked = Assert.Single((await f.As<BoardDto>(f.Reader, new GetBoard(board))).Items, item => item.Id == card).Target!;
        Assert.Equal((ContactObjectType.Id, false, (string?)null, (JsonObject?)null), (locked.Type, locked.Available, locked.Title, locked.Data));
    }

    /// <summary>VYK1-4: the objects of a board's cards are decided together, so reading a board costs the same statements for 5
    /// object cards as for 50.</summary>
    [PostgreSqlFact]
    public async Task A_board_reads_its_object_cards_in_a_constant_number_of_statements()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_board_object_cards_", settings => settings["Modules:Boards:Enabled"] = "true", shared: true);
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);

        async Task<int> StatementsAsync(int cards)
        {
            var boardId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/boards", new { title = $"Kontakty {cards}" }),
                HttpStatusCode.Created))["id"]!.GetValue<string>();
            var changes = new List<object>();
            for (var i = 0; i < cards; i++)
            {
                var contactId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/objects",
                    new { typeId = ContactObjectType.Id, scope = ObjectScopes.Wiki, data = new { name = $"Kontakt {cards}-{i}" } }), HttpStatusCode.Created))["id"]!.GetValue<string>();
                changes.Add(new { type = "create", item = new { id = Guid.NewGuid(), kind = "object", x = i * 10, y = 0, width = 280, height = 160, z = i,
                    props = new { objectId = contactId, typeId = ContactObjectType.Id, view = "card" } } });
            }
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/boards/{boardId}/operations", new { operationId = Guid.NewGuid(), changes }),
                HttpStatusCode.OK);
            var read = await admin.GetAsync($"/api/boards/{boardId}");
            Assert.Equal(cards, (await AtlasHttpSite.Json(read, HttpStatusCode.OK))["items"]!.AsArray().Count(item => item!["target"]!["available"]!.GetValue<bool>()));
            return Server.Platform.Hosting.AtlasServerTiming.Statements(string.Join(",", read.Headers.GetValues("Server-Timing")))!.Value;
        }

        var few = await StatementsAsync(5);
        var many = await StatementsAsync(50);
        Assert.True(few == many, $"{few} SQL for 5 object cards, {many} for 50");
    }

    private static CreateObject Contact(string scope, string data) => new(ContactObjectType.Id, scope, null, JsonNode.Parse(data)!.AsObject());

    private static BoardChange Place(Guid id, Guid objectId, string typeId = ContactObjectType.Id)
    {
        var props = new JsonObject { ["objectId"] = objectId.ToString(), ["typeId"] = typeId, ["view"] = "card" };
        return new BoardChange("create", Item: new BoardItemInput(id, "object", 0, 0, 280, 160, 0, Props: props));
    }
}
