using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>The <c>object</c> kind (objects spec §5.1): a live card of any object placed on the board, by its id and type, shown as
/// a card or compact; placed only by who may read the object, and never turned into another object afterwards.</summary>
public sealed class BoardObjectItemTests
{
    private static readonly Guid s_contact = Guid.Parse("00000000-0000-0000-0000-0000000000c7");
    private static readonly Guid s_hidden = Guid.Parse("00000000-0000-0000-0000-0000000000c8");
    private static readonly Guid s_new = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid s_card = Guid.Parse("00000000-0000-0000-0000-0000000000e1");

    [Fact]
    public void An_object_the_author_may_read_is_placed_by_its_id_and_type_and_is_the_item_s_target()
    {
        var result = Apply(State(), Create(Props(s_contact)));

        Assert.Null(result.Rejection);
        var item = Assert.Single(result.Changed);
        Assert.Equal(("object", (Guid?)s_contact), (item.Kind, item.TargetResourceId));
        Assert.Equal(Canonical(Props(s_contact)), item.Props);
        // The target may be named as well, as a pasted page card does, but only as the same object.
        Assert.Null(Apply(State(), Create(Props(s_contact), target: s_contact)).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Create(Props(s_contact), target: s_hidden)).Rejection);
    }

    [Theory]
    [InlineData("""{"objectId":"00000000-0000-0000-0000-0000000000c8","typeId":"atlas.contact","view":"card"}""")]
    [InlineData("""{"objectId":"00000000-0000-0000-0000-0000000000c7","typeId":"atlas.board","view":"card"}""")]
    [InlineData("""{"objectId":"00000000-0000-0000-0000-0000000000c7","typeId":"atlas.contact","view":"full"}""")]
    [InlineData("""{"objectId":"00000000-0000-0000-0000-0000000000c7","typeId":"atlas.contact"}""")]
    [InlineData("""{"objectId":"not-a-guid","typeId":"atlas.contact","view":"card"}""")]
    [InlineData("""{"objectId":"00000000-0000-0000-0000-0000000000c7","typeId":"atlas.contact","view":"card","title":"x"}""")]
    public void An_unreadable_object_another_type_or_invalid_props_refuse_the_placement(string props)
    {
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Create(JsonNode.Parse(props)!.AsObject())).Rejection);
    }

    [Fact]
    public void A_placed_card_changes_its_view_but_never_the_object_it_shows()
    {
        Assert.Equal(Canonical(Props(s_contact, "compact")),
            Assert.Single(Apply(State(), Set(new JsonObject { ["view"] = "compact" })).Changed).Props);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Set(new JsonObject { ["objectId"] = s_hidden.ToString() })).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Set(new JsonObject { ["typeId"] = "atlas.contact" })).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Create(Props(s_contact)) with { Item = Create(Props(s_contact)).Item! with { From = s_card, To = s_card } }).Rejection);
    }

    [Fact]
    public void The_objects_a_batch_places_are_read_before_it_applies_and_an_object_card_is_never_searched_on_the_board()
    {
        Assert.Equal(new[] { s_contact }, BoardEditing.ObjectTargets([Create(Props(s_contact)), BoardsFixture.Note(Guid.NewGuid(), "x"), Create(Props(s_contact))]));
        Assert.Empty(BoardEditing.ObjectTargets([Create(new JsonObject { ["objectId"] = "nope" })]));
        Assert.Null(BoardProps.SearchText("object", null, Canonical(Props(s_contact))));
    }

    private static BoardEditResult Apply(BoardState state, params BoardChange[] changes) => BoardEditing.Apply(state, changes, 5);

    private static BoardState State()
    {
        var card = new BoardItemState(s_card, "object", 0, 0, 280, 160, 0, null, null, s_contact, null, null, 1, false, Canonical(Props(s_contact)));
        return new BoardState("Board", [card], 1, new HashSet<Guid>(), new HashSet<Guid>(), ReadableObjects: new Dictionary<Guid, string> { [s_contact] = "atlas.contact" });
    }

    private static BoardChange Create(JsonObject props, Guid? target = null)
    {
        return new BoardChange("create", Item: new BoardItemInput(s_new, "object", 0, 0, 280, 160, 1, TargetResourceId: target, Props: props));
    }

    private static BoardChange Set(JsonObject props) => new("set", ItemId: s_card, Props: props);

    private static JsonObject Props(Guid objectId, string view = "card")
    {
        return new JsonObject { ["objectId"] = objectId.ToString(), ["typeId"] = "atlas.contact", ["view"] = view };
    }

    private static string Canonical(JsonObject props) => BoardJson.Write(BoardProps.Canonical(props));
}
