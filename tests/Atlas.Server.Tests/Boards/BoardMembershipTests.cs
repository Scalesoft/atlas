using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>Explicit membership of groups and frames (objects spec §5.4): an item's parent is a live group or frame of the board,
/// set and cleared by a <c>parent</c> change, never a cycle and at most four levels deep.</summary>
public sealed class BoardMembershipTests
{
    private static readonly Guid s_note = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid s_other = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid s_link = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid s_frame = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid s_deletedFrame = Guid.Parse("00000000-0000-0000-0000-0000000000f2");
    private static readonly Guid s_group = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid s_foreign = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid s_new = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    [Fact]
    public void An_item_joins_a_group_or_frame_and_leaves_it_with_a_parent_change()
    {
        var created = Apply(State(), 5,
            new BoardChange("create", Item: new BoardItemInput(s_new, "group", 0, 0, 400, 300, 9, Text: "**Tým** A", Color: "teal")),
            new BoardChange("parent", ItemId: s_note, ParentId: s_new),
            new BoardChange("parent", ItemId: s_other, ParentId: s_frame));
        Assert.Null(created.Rejection);
        var changed = created.Changed.ToDictionary(item => item.Id);
        Assert.Equal(("group", "**Tým** A", (Guid?)null), (changed[s_new].Kind, changed[s_new].Text, changed[s_new].ParentId));
        Assert.Equal((s_new, 5L), (changed[s_note].ParentId!.Value, changed[s_note].Version));
        Assert.Equal(s_frame, changed[s_other].ParentId);

        var left = Apply(State([changed[s_note]]), 6, new BoardChange("parent", ItemId: s_note));
        Assert.Equal((null, 6L), (Assert.Single(left.Changed).ParentId, Assert.Single(left.Changed).Version));
        // A group's name is one line of at most 200 characters, as a connector's label.
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 7, new BoardChange("create", Item: new BoardItemInput(s_new, "group", 0, 0, 40, 40, 0, Text: "two\nlines"))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), 7, new BoardChange("create", Item: new BoardItemInput(s_new, "group", 0, 0, 40, 40, 0, Text: new string('x', 201)))).Rejection);
    }

    [Fact]
    public void A_parent_is_a_live_group_or_frame_of_this_board_and_a_connector_has_none()
    {
        Assert.Equal(BoardRejection.Invalid, Parent(s_note, s_other).Rejection);
        Assert.Equal(BoardRejection.Invalid, Parent(s_frame, s_frame).Rejection);
        Assert.Equal(BoardRejection.Invalid, Parent(s_link, s_frame).Rejection);
        Assert.Equal(BoardRejection.ItemDeleted, Parent(s_note, s_deletedFrame).Rejection);
        Assert.Equal(BoardRejection.ItemMissing, Parent(s_note, Guid.NewGuid()).Rejection);
        Assert.Equal(BoardRejection.ItemMissing, Parent(s_note, s_foreign).Rejection);
        Assert.Equal(BoardRejection.ItemMissing, Parent(Guid.NewGuid(), s_frame).Rejection);
    }

    [Fact]
    public void Nesting_never_makes_a_cycle_nor_goes_deeper_than_four_levels()
    {
        // g1 ⊃ g2 ⊃ g3 ⊃ g4: g4 has three ancestors.
        var groups = Enumerable.Range(1, 5).Select(index => Guid.Parse($"00000000-0000-0000-0000-0000000001{index:d2}")).ToArray();
        var items = groups.Select((id, index) => Item(id, "group", index is > 0 and < 4 ? groups[index - 1] : null)).ToArray();
        var state = State(items);

        Assert.Null(Apply(state, 2, new BoardChange("parent", ItemId: s_note, ParentId: groups[3])).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(state, 2, new BoardChange("parent", ItemId: groups[0], ParentId: groups[3])).Rejection);
        // g5 with a member would put that member five levels down; alone it fits.
        Assert.Null(Apply(state, 2, new BoardChange("parent", ItemId: groups[4], ParentId: groups[3])).Rejection);
        var withMember = State([..items, Item(s_other, "note", groups[4])]);
        Assert.Equal(BoardRejection.Invalid, Apply(withMember, 2, new BoardChange("parent", ItemId: groups[4], ParentId: groups[3])).Rejection);
        // The links a batch made count for its later changes.
        Assert.Equal(BoardRejection.Invalid, Apply(state, 2,
            new BoardChange("parent", ItemId: s_group, ParentId: s_frame), new BoardChange("parent", ItemId: s_frame, ParentId: s_group)).Rejection);
    }

    [Fact]
    public void A_parent_change_touches_its_parent_and_is_normalized_to_its_own_members()
    {
        BoardChange[] changes = [new("parent", ItemId: s_note, ParentId: s_frame, Text: "ignored"), new("parent", ItemId: s_other)];
        Assert.Equal(new[] { s_note, s_frame, s_other }.Order(), BoardEditing.TouchedIds(changes).Order());
        Assert.Equal([new BoardChange("parent", ItemId: s_note, ParentId: s_frame), new BoardChange("parent", ItemId: s_other)], BoardEditing.Normalize(changes));
        Assert.True(BoardEditing.ChangesParents(changes));
        Assert.False(BoardEditing.ChangesParents([new BoardChange("delete", ItemId: s_note)]));
    }

    private static BoardEditResult Parent(Guid item, Guid parent)
    {
        return Apply(State(), 2, new BoardChange("parent", ItemId: item, ParentId: parent));
    }

    private static BoardEditResult Apply(BoardState state, long sequence, params BoardChange[] changes)
    {
        return BoardEditing.Apply(state, changes, sequence);
    }

    private static BoardItemState Item(Guid id, string kind, Guid? parent = null, bool deleted = false)
    {
        return new BoardItemState(id, kind, 0, 0, 160, 120, 0, null, null, null, kind == "connector" ? s_note : null, kind == "connector" ? s_other : null, 1, deleted,
            ParentId: parent);
    }

    /// <summary>Two notes, a connector between them, a frame, a deleted frame and a group; s_foreign is an item of another board.
    /// The parent links are the whole board's, as the handler loads them for a batch that changes parents.</summary>
    private static BoardState State(BoardItemState[]? added = null)
    {
        var items = new Dictionary<Guid, BoardItemState>
        {
            [s_note] = Item(s_note, "note"),
            [s_other] = Item(s_other, "note"),
            [s_link] = Item(s_link, "connector"),
            [s_frame] = Item(s_frame, "frame"),
            [s_deletedFrame] = Item(s_deletedFrame, "frame", deleted: true),
            [s_group] = Item(s_group, "group")
        };
        foreach (var item in added ?? [])
        {
            items[item.Id] = item;
        }
        var parents = items.Values.Where(item => item.ParentId is not null).ToDictionary(item => item.Id, item => item.ParentId!.Value);
        return new BoardState("Board", items.Values.ToArray(), items.Values.Count(item => !item.IsDeleted), new HashSet<Guid> { s_foreign }, new HashSet<Guid>(),
            Parents: parents);
    }
}
