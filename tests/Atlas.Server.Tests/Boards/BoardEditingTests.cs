using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>The pure rules of a batch (boards spec §5): applied in order over the committed state of the touched items, all or
/// nothing, with typed rejections.</summary>
public sealed class BoardEditingTests
{
    private static readonly Guid s_note = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid s_other = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid s_deleted = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid s_link = Guid.Parse("00000000-0000-0000-0000-0000000000a4");
    private static readonly Guid s_page = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid s_hiddenPage = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid s_foreign = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid s_new = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid s_new2 = Guid.Parse("00000000-0000-0000-0000-0000000000d2");

    /// <summary>L1: a reader catching up gets each text, title, props and settings key as the last batch left it, never a replaced value;
    /// a key a later set removed is left out of the create's full props and stays a removal in the earlier set.</summary>
    [Fact]
    public void A_reader_catches_up_with_the_last_values_and_never_a_replaced_one()
    {
        var created = new BoardItemInput(s_new, "text", 0, 0, 200, 100, 1, Text: "heslo: tajne123",
            Props: System.Text.Json.Nodes.JsonNode.Parse("""{"align":"left","bold":true,"fontSize":"s"}""")!.AsObject());
        BoardBatchDto Batch(long sequence, params BoardChange[] changes) => new(sequence, Guid.NewGuid(), Guid.NewGuid(), changes);
        var batches = BoardEditing.LatestValues([
            Batch(1, new BoardChange("create", Item: created), new BoardChange("rename", Title: "Tajný plán"),
                new BoardChange("settings", Settings: System.Text.Json.Nodes.JsonNode.Parse("""{"gridSize":8}""")!.AsObject())),
            Batch(2, new BoardChange("set", ItemId: s_new, Text: "smazáno", Props: System.Text.Json.Nodes.JsonNode.Parse("""{"bold":null,"fontSize":"m"}""")!.AsObject()),
                new BoardChange("move", ItemId: s_new, X: 5, Y: 5, Width: 200, Height: 100)),
            Batch(3, new BoardChange("rename", Title: "Plán"), new BoardChange("settings", Settings: System.Text.Json.Nodes.JsonNode.Parse("""{"gridSize":16}""")!.AsObject()))]);

        var told = System.Text.Json.JsonSerializer.Serialize(batches);
        Assert.DoesNotContain("tajne123", told);
        Assert.DoesNotContain("Tajn", told);
        var item = batches[0].Changes[0].Item!;
        Assert.Equal(("smazáno", """{"align":"left","fontSize":"m"}"""), (item.Text, item.Props!.ToJsonString()));
        Assert.Equal(("Plán", """{"gridSize":16}"""), (batches[0].Changes[1].Title, batches[0].Changes[2].Settings!.ToJsonString()));
        Assert.Equal(("smazáno", """{"bold":null,"fontSize":"m"}"""), (batches[1].Changes[0].Text, batches[1].Changes[0].Props!.ToJsonString()));
        Assert.Equal(new BoardChange("move", ItemId: s_new, X: 5, Y: 5, Width: 200, Height: 100), batches[1].Changes[1]);
        Assert.Equal([1L, 2L, 3L], batches.Select(batch => batch.Sequence));
    }

    [Fact]
    public void Every_change_type_applies_in_order_and_stamps_what_it_touched_with_the_batch_sequence()
    {
        var result = Apply(State(), 7,
            Create(Note(s_new) with { Text = "hello", Color = "blue" }),
            Create(new BoardItemInput(s_new2, "page", 0, 0, 200, 100, 1, TargetResourceId: s_page, Props: BoardsFixture.Card())),
            Create(new BoardItemInput(Guid.NewGuid(), "connector", 0, 0, 8, 8, 2, From: s_new, To: s_new2, Props: BoardsFixture.Elbow())),
            new BoardChange("move", ItemId: s_note, X: -5.5, Y: 10, Width: 300, Height: 120),
            new BoardChange("set", ItemId: s_note, Text: "edited"),
            new BoardChange("reorder", ItemId: s_other, Z: -3),
            new BoardChange("delete", ItemId: s_other),
            new BoardChange("restore", ItemId: s_deleted),
            new BoardChange("rename", Title: "  Plan  "));

        Assert.Null(result.Rejection);
        Assert.Equal("Plan", result.Title);
        var changed = result.Changed.ToDictionary(item => item.Id);
        Assert.Equal(6, changed.Count);
        Assert.All(changed.Values, item => Assert.Equal(7, item.Version));
        Assert.Equal(("note", "hello", "blue", false), (changed[s_new].Kind, changed[s_new].Text, changed[s_new].Color, changed[s_new].IsDeleted));
        Assert.Equal(s_page, changed[s_new2].TargetResourceId);
        Assert.Single(changed.Values, item => item.Kind == "connector" && item.FromItemId == s_new && item.ToItemId == s_new2);
        // move sets the geometry as a whole and nothing else; set changes only what it names.
        Assert.Equal((-5.5, 10d, 300d, 120d, "edited", "yellow"), (changed[s_note].X, changed[s_note].Y, changed[s_note].Width, changed[s_note].Height,
            changed[s_note].Text, changed[s_note].Color));
        Assert.Equal((-3, true), (changed[s_other].Z, changed[s_other].IsDeleted));
        Assert.False(changed[s_deleted].IsDeleted);
        // 3 live + 3 created - 1 deleted + 1 restored.
        Assert.Equal(6, result.LiveItemCount);
    }

    [Fact]
    public void A_later_batch_wins_per_property_so_two_writers_of_different_properties_both_keep_theirs()
    {
        var first = Apply(State(), 2, new BoardChange("set", ItemId: s_note, Text: "first writer"));
        var second = Apply(State(first.Changed.ToArray()), 3, new BoardChange("set", ItemId: s_note, Color: "pink"));
        var item = Assert.Single(second.Changed);
        Assert.Equal(("first writer", "pink", 3L), (item.Text, item.Color, item.Version));
        // The same property again: the later one is the value.
        var third = Apply(State(second.Changed.ToArray()), 4, new BoardChange("set", ItemId: s_note, Text: "second writer"));
        Assert.Equal(("second writer", "pink"), (Assert.Single(third.Changed).Text, Assert.Single(third.Changed).Color));
    }

    [Fact]
    public void A_rejected_change_rejects_the_batch_with_nothing_applied()
    {
        var state = State();
        var result = Apply(state, 9,
            new BoardChange("rename", Title: "Renamed"),
            Create(Note(s_new)),
            new BoardChange("move", ItemId: s_note, X: 1, Y: 1, Width: 10, Height: 10),
            new BoardChange("set", ItemId: Guid.NewGuid(), Text: "nobody"));
        Assert.Equal(BoardRejection.ItemMissing, result.Rejection);
        Assert.Empty(result.Changed);
        Assert.Equal("Board", result.Title);
        Assert.Equal(state.LiveItemCount, result.LiveItemCount);
        Assert.Equal(0, Assert.Single(state.Items, item => item.Id == s_note).X);
    }

    [Fact]
    public void Unknown_deleted_and_existing_items_are_told_apart_by_code()
    {
        var unknown = Guid.NewGuid();
        foreach (var change in new[]
                 {
                     new BoardChange("move", ItemId: unknown, X: 0, Y: 0, Width: 10, Height: 10),
                     new BoardChange("set", ItemId: unknown, Text: "x"),
                     new BoardChange("reorder", ItemId: unknown, Z: 1),
                     new BoardChange("delete", ItemId: unknown),
                     new BoardChange("restore", ItemId: unknown),
                     // An item of another board is unknown here, whatever it is there.
                     new BoardChange("set", ItemId: s_foreign, Text: "x")
                 })
        {
            Assert.Equal(BoardRejection.ItemMissing, Apply(State(), 2, change).Rejection);
        }
        foreach (var change in new[]
                 {
                     new BoardChange("move", ItemId: s_deleted, X: 0, Y: 0, Width: 10, Height: 10),
                     new BoardChange("set", ItemId: s_deleted, Color: "gray"),
                     new BoardChange("reorder", ItemId: s_deleted, Z: 1),
                     new BoardChange("delete", ItemId: s_deleted)
                 })
        {
            Assert.Equal(BoardRejection.ItemDeleted, Apply(State(), 2, change).Rejection);
        }
        // A new id must be new everywhere: on this board (deleted items included) and on any other.
        foreach (var taken in new[] { s_note, s_deleted, s_foreign })
        {
            Assert.Equal(BoardRejection.ItemExists, Apply(State(), 2, Create(Note(taken))).Rejection);
        }
        Assert.Equal(BoardRejection.ItemExists, Apply(State(), 2, Create(Note(s_new)), Create(Note(s_new))).Rejection);
        // Restoring a live item is no change at all.
        var restored = Apply(State(), 2, new BoardChange("restore", ItemId: s_note));
        Assert.Null(restored.Rejection);
        Assert.Empty(restored.Changed);
    }

    [Fact]
    public void A_connector_joins_two_different_live_items_of_the_board_that_are_not_connectors()
    {
        Assert.Null(Apply(State(), 2, Connector(s_note, s_other)).Rejection);
        // An end created earlier in the same batch counts.
        Assert.Null(Apply(State(), 2, Create(Note(s_new)), Connector(s_new, s_note)).Rejection);
        Assert.Equal(BoardRejection.ItemMissing, Apply(State(), 2, Connector(s_note, Guid.NewGuid())).Rejection);
        Assert.Equal(BoardRejection.ItemMissing, Apply(State(), 2, Connector(s_foreign, s_note)).Rejection);
        Assert.Equal(BoardRejection.ItemDeleted, Apply(State(), 2, Connector(s_note, s_deleted)).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Connector(s_note, s_link)).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Connector(s_note, s_note)).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Create(new BoardItemInput(s_new, "connector", 0, 0, 8, 8, 0, From: s_note))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Create(new BoardItemInput(s_new, "connector", 0, 0, 8, 8, 0,
            TargetResourceId: s_page, From: s_note, To: s_other))).Rejection);
    }

    [Fact]
    public void A_page_card_needs_a_page_the_author_may_read_and_a_note_carries_neither_a_target_nor_ends()
    {
        Assert.Null(Apply(State(), 2, Create(new BoardItemInput(s_new, "page", 0, 0, 200, 100, 0, TargetResourceId: s_page, Props: BoardsFixture.Card()))).Rejection);
        // An unreadable or unknown page and a missing target look the same: invalid, with no hint which.
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Create(new BoardItemInput(s_new, "page", 0, 0, 200, 100, 0, TargetResourceId: s_hiddenPage, Props: BoardsFixture.Card()))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Create(new BoardItemInput(s_new, "page", 0, 0, 200, 100, 0, Props: BoardsFixture.Card()))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Create(new BoardItemInput(s_new, "page", 0, 0, 200, 100, 0, TargetResourceId: s_page, From: s_note, Props: BoardsFixture.Card()))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Create(Note(s_new) with { TargetResourceId = s_page })).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Create(Note(s_new) with { To = s_note })).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Create(Note(s_new) with { Kind = "sticker" })).Rejection);
    }

    public static TheoryData<BoardChange> InvalidChanges => new()
    {
        // Every number finite and in range: x and y within ±1 000 000, width and height 8–20 000, z an int32.
        Create(Note(s_new) with { X = double.NaN }),
        Create(Note(s_new) with { Y = double.PositiveInfinity }),
        Create(Note(s_new) with { Width = double.NegativeInfinity }),
        Create(Note(s_new) with { X = 1_000_000.001 }),
        Create(Note(s_new) with { Y = -1_000_000.001 }),
        Create(Note(s_new) with { Width = 7.999 }),
        Create(Note(s_new) with { Height = 20_000.001 }),
        Create(Note(s_new) with { Z = (long)int.MaxValue + 1 }),
        Create(Note(s_new) with { Z = null }),
        Create(Note(s_new) with { Height = null }),
        Create(Note(s_new) with { Id = Guid.Empty }),
        Create(Note(s_new) with { Text = new string('x', 4001) }),
        Create(Note(s_new) with { Color = "brown" }),
        new BoardChange("create"),
        new BoardChange("move", ItemId: s_note, X: double.NaN, Y: 0, Width: 10, Height: 10),
        new BoardChange("move", ItemId: s_note, X: 0, Y: 0, Width: 10),
        new BoardChange("move", X: 0, Y: 0, Width: 10, Height: 10),
        new BoardChange("set", ItemId: s_note),
        new BoardChange("set", ItemId: s_note, Text: new string('x', 4001)),
        new BoardChange("set", ItemId: s_note, Color: "Yellow"),
        new BoardChange("reorder", ItemId: s_note),
        new BoardChange("reorder", ItemId: s_note, Z: (long)int.MinValue - 1),
        new BoardChange("rename", Title: "   "),
        new BoardChange("rename", Title: new string('x', 201)),
        new BoardChange("rename", Title: "two\nlines"),
        new BoardChange("rename", Title: "nul\0"),
        new BoardChange("rename", Title: "tab\there"),
        new BoardChange("rename", Title: "‮evil"),
        // Note text is several lines (the house rule of a description): \n and \r only, no other control or bidi character.
        Create(Note(s_new) with { Text = "nul\0" }),
        new BoardChange("set", ItemId: s_note, Text: "a\0b"),
        new BoardChange("set", ItemId: s_note, Text: "bell\u0007"),
        new BoardChange("set", ItemId: s_note, Text: "tab\there"),
        new BoardChange("set", ItemId: s_note, Text: "evil‮txt"),
        new BoardChange("set", ItemId: s_note, Text: "isolate⁦"),
        new BoardChange("rename"),
        new BoardChange("resize", ItemId: s_note),
        new BoardChange(null, ItemId: s_note),
    };

    [Theory]
    [MemberData(nameof(InvalidChanges))]
    public void Malformed_or_out_of_range_changes_are_invalid(BoardChange change)
    {
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, change).Rejection);
    }

    [Fact]
    public void Limits_hold_exactly_at_their_bounds()
    {
        var bounds = Note(s_new) with { X = -1_000_000, Y = 1_000_000, Width = 8, Height = 20_000, Z = int.MinValue, Text = new string('x', 4000) };
        Assert.Null(Apply(State(), 2, Create(bounds)).Rejection);
        Assert.Null(Apply(State(), 2, new BoardChange("reorder", ItemId: s_note, Z: int.MaxValue)).Rejection);
        Assert.Null(Apply(State(), 2, new BoardChange("rename", Title: new string('x', 200))).Rejection);
        Assert.Equal("line 1\r\nline 2\n", Assert.Single(Apply(State(), 2, new BoardChange("set", ItemId: s_note, Text: "line 1\r\nline 2\n")).Changed).Text);

        // 1–200 changes per batch.
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2).Rejection);
        Assert.Equal(BoardRejection.Invalid, BoardEditing.Apply(State(), null, 2).Rejection);
        Assert.Null(Apply(State(), 2, Enumerable.Repeat(new BoardChange("set", ItemId: s_note, Text: "x"), 200).ToArray()).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 2, Enumerable.Repeat(new BoardChange("set", ItemId: s_note, Text: "x"), 201).ToArray()).Rejection);
        Assert.Equal(BoardRejection.Invalid, BoardEditing.Apply(State(), [null!], 2).Rejection);

        // At most 5000 live items: a create or a restore past it is refused, deleting first makes room.
        Assert.Null(Apply(State(liveItemCount: 4999), 2, Create(Note(s_new))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(liveItemCount: 5000), 2, Create(Note(s_new))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(liveItemCount: 5000), 2, new BoardChange("restore", ItemId: s_deleted)).Rejection);
        Assert.Null(Apply(State(liveItemCount: 5000), 2, new BoardChange("delete", ItemId: s_other), Create(Note(s_new))).Rejection);
    }

    [Fact]
    public void A_connector_whose_end_is_deleted_is_not_shown_and_comes_back_when_the_end_is_restored()
    {
        var items = State().Items.ToList();
        var link = Assert.Single(items, item => item.Id == s_link);
        Assert.Equal([s_note, s_other, s_link], BoardEditing.Visible(items).Select(item => item.Id));

        var deleted = Apply(State(), 2, new BoardChange("delete", ItemId: s_other)).Changed.Single();
        var afterDelete = items.Select(item => item.Id == s_other ? deleted : item).ToList();
        Assert.Equal([s_note], BoardEditing.Visible(afterDelete).Select(item => item.Id));
        // The connector itself is not deleted: a change of it is still accepted while hidden.
        Assert.Null(Apply(State(afterDelete.ToArray()), 3, new BoardChange("set", ItemId: s_link, Text: "label")).Rejection);

        var restored = Apply(State(afterDelete.ToArray()), 3, new BoardChange("restore", ItemId: s_other)).Changed.Single();
        var afterRestore = afterDelete.Select(item => item.Id == s_other ? restored : item).ToList();
        Assert.Contains(link, BoardEditing.Visible(afterRestore));
        // An end that does not exist at all hides it too.
        Assert.DoesNotContain(BoardEditing.Visible([link]), item => item.Id == s_link);
    }

    [Fact]
    public void The_touched_ids_and_page_targets_of_a_batch_are_what_the_handler_loads()
    {
        BoardChange[] changes =
        [
            Create(new BoardItemInput(s_new, "connector", 0, 0, 8, 8, 0, From: s_note, To: s_other)),
            Create(new BoardItemInput(s_new2, "page", 0, 0, 8, 8, 0, TargetResourceId: s_page)),
            new BoardChange("delete", ItemId: s_deleted),
            new BoardChange("rename", Title: "x")
        ];
        Assert.Equal(new[] { s_new, s_note, s_other, s_new2, s_deleted }.Order(), BoardEditing.TouchedIds(changes).Order());
        Assert.Equal([s_page], BoardEditing.PageTargets(changes));
        Assert.Empty(BoardEditing.TouchedIds(null));
    }

    private static BoardEditResult Apply(BoardState state, long sequence, params BoardChange[] changes)
    {
        return BoardEditing.Apply(state, changes, sequence);
    }

    private static BoardChange Create(BoardItemInput item)
    {
        return new BoardChange("create", Item: item);
    }

    private static BoardChange Connector(Guid from, Guid to)
    {
        return Create(new BoardItemInput(s_new2, "connector", 0, 0, 8, 8, 0, From: from, To: to, Props: BoardsFixture.Elbow()));
    }

    private static BoardItemInput Note(Guid id)
    {
        return new BoardItemInput(id, "note", 10, 20, 160, 120, 0);
    }

    /// <summary>A note, a second note, a deleted note and a connector between the two live ones; s_page is readable, s_foreign is
    /// an item of another board.</summary>
    private static BoardState State(BoardItemState[]? replaced = null, int liveItemCount = 3)
    {
        var items = new Dictionary<Guid, BoardItemState>
        {
            [s_note] = new(s_note, "note", 0, 0, 160, 120, 0, "note", "yellow", null, null, null, 1, false),
            [s_other] = new(s_other, "note", 200, 0, 160, 120, 1, null, "green", null, null, null, 1, false),
            [s_deleted] = new(s_deleted, "note", 400, 0, 160, 120, 2, null, null, null, null, null, 1, true),
            [s_link] = new(s_link, "connector", 0, 0, 8, 8, 3, null, null, null, s_note, s_other, 1, false)
        };
        foreach (var item in replaced ?? [])
        {
            items[item.Id] = item;
        }
        return new BoardState("Board", items.Values.ToArray(), liveItemCount, new HashSet<Guid> { s_foreign }, new HashSet<Guid> { s_page });
    }
}
