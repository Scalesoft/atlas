using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>The typed <c>props</c> of every kind (interaction spec §4): full on create, partial on set merged per key, strictly
/// validated (an unknown key or value is invalid_board_change), stored and broadcast in one canonical form.</summary>
public sealed class BoardPropsTests
{
    private static readonly Guid s_new = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid s_note = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid s_other = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid s_text = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid s_link = Guid.Parse("00000000-0000-0000-0000-0000000000a4");
    private static readonly Guid s_page = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    /// <summary>The editor's contact card, as a page holds one.</summary>
    private const string Contact = """{"block":{"type":"contactCard","attrs":{"data":{"version":1,"contacts":[{"name":"Jana Nováková","title":"Projektová manažerka","organization":"Modelsoft","emails":["jana@example.com"],"phones":["+420 123 456 789"]}]}}}}""";

    public static TheoryData<string, string> ValidCreates => new()
    {
        { "note", "{}" },
        { "note", """{"fontSize":"auto"}""" },
        { "text", """{"fontSize":"xl","align":"center","bold":true}""" },
        { "text", """{"fontSize":"s","align":"left"}""" },
        { "shape", """{"shape":"diamond","fontSize":"l","align":"right"}""" },
        { "shape", """{"shape":"rounded"}""" },
        { "frame", "{}" },
        { "group", "{}" },
        { "group", """{"collapsed":true}""" },
        { "page", """{"display":"card"}""" },
        { "page", """{"display":"preview","anchor":"planovani-2026"}""" },
        { "page", """{"display":"product","anchor":"portal-pacienta"}""" },
        { "page", """{"display":"service","anchor":"nia"}""" },
        { "block", Contact },
        { "block", """{"block":{"type":"paragraph","content":[{"type":"text","text":"Úkol "},{"type":"youtrackIssue","attrs":{"id":"2-12","key":"ATL-12","title":"Opravit přihlášení","url":"https://youtrack.example.com/issue/ATL-12","state":"Open"}}]}}""" },
        { "block", """{"block":{"type":"callout","attrs":{"variant":"warning"},"content":[{"type":"paragraph","content":[{"type":"text","text":"Pozor"}]}]}}""" },
        // An inline node is one when a paragraph holds just it, as the web sends a YouTrack issue.
        { "block", """{"block":{"type":"youtrackIssue","attrs":{"id":"2-12","key":"ATL-12","title":"Opravit přihlášení","url":"https://youtrack.example.com/issue/ATL-12","state":null}}}""" },
        // A list derived from the relations (P15): what it shows, and one page or object when it shows that one's.
        { "links", """{"show":"people"}""" },
        { "links", """{"objectId":"00000000-0000-0000-0000-0000000000b1","show":"waiting"}""" },
        { "links", """{"objectId":"00000000-0000-0000-0000-0000000000b1","show":"usage"}""" },
    };

    [Theory]
    [MemberData(nameof(ValidCreates))]
    public void Every_kind_takes_its_full_props_on_create(string kind, string props)
    {
        var result = Apply(State(), Create(kind, props, target: kind == "page" ? s_page : null));
        Assert.Null(result.Rejection);
        Assert.Equal(Canonical(props), Assert.Single(result.Changed).Props);
    }

    public static TheoryData<string, string> InvalidCreates => new()
    {
        { "note", """{"fontSize":"xl"}""" },
        { "note", """{"align":"left"}""" },
        { "note", """{"fontSize":null}""" },
        { "text", """{"fontSize":"m"}""" },
        { "text", """{"align":"left"}""" },
        { "text", """{"fontSize":"m","align":"justify"}""" },
        { "text", """{"fontSize":"m","align":"left","bold":"yes"}""" },
        { "shape", "{}" },
        { "shape", """{"shape":"star"}""" },
        { "shape", """{"shape":"rect","fontSize":12}""" },
        { "frame", """{"title":"x"}""" },
        { "group", """{"collapsed":"yes"}""" },
        { "group", """{"color":"red"}""" },
        { "page", "{}" },
        { "page", """{"display":"full"}""" },
        { "page", """{"display":"requirement","anchor":"nia"}""" },
        { "page", """{"display":"card","anchor":"v3-h2"}""" },
        { "page", """{"display":"card","anchor":"-starts-badly"}""" },
        { "page", """{"display":"card","anchor":"with space"}""" },
        { "page", """{"display":"card","anchor":"line\n"}""" },
        { "links", "{}" },
        { "links", """{"show":"everything"}""" },
        { "links", """{"show":"usage","objectId":"not-a-guid"}""" },
        { "links", """{"show":"people","rows":["Jana"]}""" },
        { "block", "{}" },
        { "block", """{"block":"contactCard"}""" },
        { "block", """{"block":{"type":"widget"}}""" },
        { "block", """{"block":{"type":"youtrackIssue","attrs":{"key":"ATL-12","title":"x","url":"https://youtrack.example.com/issue/ATL-12"}}}""" },
        { "block", """{"block":{"type":"paragraph","content":[{"type":"paragraph"}]}}""" },
        { "block", """{"block":{"type":"secretInlineReference","attrs":{"secretId":"00000000-0000-0000-0000-000000000001","field":"password"}}}""" },
        { "block", """{"block":{"type":"contactCard","attrs":{"data":{"version":1,"contacts":[{"name":"Jana","emails":["not-an-address"]}]}}}}""" },
        { "block", """{"block":{"type":"secretReference","attrs":{"secretId":"00000000-0000-0000-0000-000000000001","field":"password"}}}""" },
        { "block", """{"block":{"type":"paragraph"},"title":"x"}""" },
        { "connector", """{"arrowStart":"none","arrowEnd":"arrow","dash":"solid"}""" },
        { "connector", """{"route":"zigzag","arrowStart":"none","arrowEnd":"arrow","dash":"solid"}""" },
        { "connector", """{"route":"elbow","arrowStart":"none","arrowEnd":"arrow","dash":"solid","fromSide":"middle"}""" },
        { "connector", """{"route":"elbow","arrowStart":"none","arrowEnd":"diamond","dash":"solid"}""" },
        { "connector", """{"route":"elbow","arrowStart":"none","arrowEnd":"arrow","dash":"solid","state":"late"}""" },
        { "connector", """{"route":"elbow","arrowStart":"none","arrowEnd":"arrow","dash":"solid","animated":"yes"}""" },
        { "connector", """{"route":"elbow","arrowStart":"none","arrowEnd":"arrow","dash":"solid","thickness":"xl"}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidCreates))]
    public void An_unknown_missing_or_invalid_prop_refuses_the_create(string kind, string props)
    {
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Create(kind, props, target: kind == "page" ? s_page : null)).Rejection);
    }

    [Fact]
    public void A_connector_takes_its_full_props_and_a_one_line_label_of_at_most_200_characters()
    {
        const string props = """{"route":"curved","arrowStart":"arrow","arrowEnd":"none","dash":"dashed","fromSide":"right","toSide":"left","state":"blocked"}""";
        var created = Apply(State(), Connector(props, "depends on"));
        Assert.Equal(Canonical(props), Assert.Single(created.Changed).Props);
        Assert.Null(Apply(State(), Connector(props, new string('x', 200))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Connector(props, new string('x', 201))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Connector(props, "two\nlines")).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), new BoardChange("set", ItemId: s_link, Text: "two\nlines")).Rejection);
        Assert.Null(Apply(State(), new BoardChange("set", ItemId: s_link, Text: "label")).Rejection);
        // A note's text keeps its 4000 characters on several lines.
        Assert.Null(Apply(State(), new BoardChange("set", ItemId: s_note, Text: "line\n" + new string('x', 3990))).Rejection);
    }

    [Fact]
    public void Only_a_page_has_a_target_and_only_a_connector_has_ends()
    {
        Assert.Null(Apply(State(), Create("page", """{"display":"card"}""", target: s_page)).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Create("page", """{"display":"card"}""")).Rejection);
        foreach (var kind in new[] { "note", "text", "shape", "frame", "group", "block" })
        {
            var props = ValidCreates.First(row => (string)row[0] == kind)[1].ToString()!;
            Assert.Equal(BoardRejection.Invalid, Apply(State(), Create(kind, props, target: s_page)).Rejection);
            Assert.Equal(BoardRejection.Invalid, Apply(State(), Create(kind, props) with { Item = Create(kind, props).Item! with { From = s_note, To = s_other } }).Rejection);
        }
    }

    [Fact]
    public void A_block_of_at_most_64_KiB_passes_and_a_larger_one_is_refused()
    {
        static string Paragraph(int length) => $$$"""{"block":{"type":"paragraph","content":[{"type":"text","text":"{{{new string('x', length)}}}"}]}}""";
        // The block alone, without its text, is {"content":[{"text":"","type":"text"}],"type":"paragraph"}: 58 bytes.
        var fits = BoardProps.MaxBlockBytes - 58;
        Assert.Equal(BoardProps.MaxBlockBytes, BoardJson.Write(Json(Paragraph(fits))["block"]!).Length);
        Assert.Null(Apply(State(), Create("block", Paragraph(fits))).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Create("block", Paragraph(fits + 1))).Rejection);
    }

    [Fact]
    public void The_board_settings_merge_per_key_over_their_defaults()
    {
        Assert.Equal(BoardProps.DefaultSettings, BoardProps.WithDefaults("{}"));
        var result = BoardEditing.Apply(State(), [new BoardChange("settings", Settings: Json("""{"background":"timeline","timeline":{"origin":"2026-01-01","pxPerDay":12.5}}"""))], 7);
        Assert.Equal("""{"background":"timeline","timeline":{"origin":"2026-01-01","pxPerDay":12.5}}""", result.Settings);
        var later = BoardEditing.Apply(State() with { Settings = result.Settings }, [new BoardChange("settings", Settings: Json("""{"gridSize":16,"snapToGrid":true,"timeline":null}"""))], 8);
        Assert.Equal("""{"background":"timeline","gridSize":16,"snapToGrid":true}""", later.Settings);
        Assert.Equal("""{"background":"timeline","gridSize":16,"snapToGrid":true}""", BoardProps.WithDefaults(later.Settings));
        Assert.Empty(later.Changed);
        foreach (var invalid in new[]
                 {
                     "{}", """{"background":"stripes"}""", """{"gridSize":20}""", """{"gridSize":"24"}""", """{"snapToGrid":null}""", """{"zoom":2}""",
                     """{"timeline":{"origin":"2026-02-30","pxPerDay":1}}""", """{"timeline":{"origin":"2026-01-01"}}""",
                     """{"timeline":{"origin":"2026-01-01","pxPerDay":0.001}}""", """{"timeline":{"origin":"2026-01-01","pxPerDay":2001}}""",
                     """{"timeline":{"origin":"2026-01-01","pxPerDay":1,"unit":"day"}}"""
                 })
        {
            Assert.Equal(BoardRejection.Invalid, BoardEditing.Apply(State(), [new BoardChange("settings", Settings: Json(invalid))], 7).Rejection);
        }
        Assert.Equal(BoardRejection.Invalid, BoardEditing.Apply(State(), [new BoardChange("settings")], 7).Rejection);
        Assert.Equal("""{"gridSize":48}""", Props(BoardEditing.Normalize([new BoardChange("settings", Settings: Json("""{"gridSize":48}"""), Title: "x")])[0]));
        Assert.Null(BoardEditing.Normalize([new BoardChange("settings", Settings: Json("""{"gridSize":48}"""), Title: "x")])[0].Title);
    }

    [Fact]
    public void The_palette_has_eleven_colours()
    {
        Assert.Equal(["yellow", "orange", "red", "pink", "purple", "blue", "teal", "green", "gray", "white", "black"], BoardEditing.Palette);
        Assert.Null(Apply(State(), new BoardChange("set", ItemId: s_note, Color: "teal")).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), new BoardChange("set", ItemId: s_note, Color: "brown")).Rejection);
    }

    /// <summary>An editor component's background comes from the appearance palette of spaces; its legacy names are not board colours.</summary>
    [Fact]
    public void An_item_takes_a_colour_of_the_appearance_palette()
    {
        Assert.Null(Apply(State(), new BoardChange("set", ItemId: s_note, Color: "blue-4")).Rejection);
        Assert.Null(Apply(State(), new BoardChange("set", ItemId: s_note, Color: "slate-1")).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), new BoardChange("set", ItemId: s_note, Color: "blue-6")).Rejection);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), new BoardChange("set", ItemId: s_note, Color: "coral")).Rejection);
    }

    /// <summary>A set with <c>color: ""</c> removes the colour (as <c>text: ""</c> empties the text): an item goes back to no colour (a
    /// requirement card to its state's colour) and an undo of the first colouring has a value to write back. A create takes none.</summary>
    [Fact]
    public void An_empty_colour_removes_the_colour_and_is_kept_as_such_in_the_log()
    {
        var coloured = Assert.Single(Apply(State(), new BoardChange("set", ItemId: s_note, Color: "teal")).Changed);
        var cleared = Apply(State([coloured]), new BoardChange("set", ItemId: s_note, Color: ""));
        Assert.Null(cleared.Rejection);
        Assert.Null(Assert.Single(cleared.Changed).Color);
        Assert.Equal("", Assert.Single(BoardEditing.Normalize([new BoardChange("set", ItemId: s_note, Color: "")])).Color);
        Assert.Equal(BoardRejection.Invalid, Apply(State(), Create("note", "{}") with { Item = Create("note", "{}").Item! with { Color = "" } }).Rejection);
    }

    [Fact]
    public void A_connector_takes_an_animated_flow_and_a_thickness_and_drops_them_with_null()
    {
        const string props = """{"route":"elbow","arrowStart":"arrow","arrowEnd":"arrow","dash":"solid","animated":true,"thickness":"l"}""";
        Assert.Equal(Canonical(props), Assert.Single(Apply(State(), Connector(props, null)).Changed).Props);
        var set = Assert.Single(Apply(State(), new BoardChange("set", ItemId: s_link, Props: Json("""{"animated":true,"thickness":"s"}"""))).Changed);
        Assert.Contains("\"animated\":true", set.Props);
        Assert.Contains("\"thickness\":\"s\"", set.Props);
        var cleared = Assert.Single(Apply(State([set]), new BoardChange("set", ItemId: s_link, Props: Json("""{"animated":null,"thickness":null}"""))).Changed);
        Assert.DoesNotContain("animated", cleared.Props);
        Assert.DoesNotContain("thickness", cleared.Props);
    }

    /// <summary>set changes only the keys it names; null removes an optional key. The normalized change keeps exactly those keys
    /// (null included), so a client that kept the values before it can undo the change with the same shape.</summary>
    [Fact]
    public void A_partial_set_merges_per_key_removes_optional_keys_with_null_and_its_inverse_restores_the_props()
    {
        var before = State().Items.Single(item => item.Id == s_text).Props;
        var change = new BoardChange("set", ItemId: s_text, Props: Json("""{"bold":null,"fontSize":"xl"}"""));
        Assert.Equal("""{"bold":null,"fontSize":"xl"}""", Props(Assert.Single(BoardEditing.Normalize([change]))));
        var after = Assert.Single(Apply(State(), change).Changed);
        Assert.Equal("""{"align":"center","fontSize":"xl"}""", after.Props);
        Assert.Equal(7, after.Version);

        // The inverse a client builds from the values it had: the removed key comes back, the changed one returns.
        var undo = Apply(State([after]), new BoardChange("set", ItemId: s_text, Props: Json("""{"bold":true,"fontSize":"m"}""")));
        Assert.Equal(before, Assert.Single(undo.Changed).Props);
    }

    [Fact]
    public void Two_writers_of_different_props_keys_both_keep_theirs_and_the_later_one_wins_a_shared_key()
    {
        var first = Assert.Single(Apply(State(), new BoardChange("set", ItemId: s_text, Props: Json("""{"align":"right"}"""))).Changed);
        var second = Assert.Single(Apply(State([first]), new BoardChange("set", ItemId: s_text, Props: Json("""{"fontSize":"s"}"""))).Changed);
        Assert.Equal("""{"align":"right","bold":true,"fontSize":"s"}""", second.Props);
        var third = Assert.Single(Apply(State([second]), new BoardChange("set", ItemId: s_text, Props: Json("""{"align":"left"}"""))).Changed);
        Assert.Equal("""{"align":"left","bold":true,"fontSize":"s"}""", third.Props);
    }

    public static TheoryData<string> InvalidSets => new()
    {
        "{}",
        """{"align":null}""",
        """{"fontSize":"huge"}""",
        """{"shape":"rect"}""",
        """{"bold":1}""",
    };

    [Theory]
    [MemberData(nameof(InvalidSets))]
    public void A_set_of_an_unknown_required_removed_or_invalid_key_is_refused(string props)
    {
        Assert.Equal(BoardRejection.Invalid, Apply(State(), new BoardChange("set", ItemId: s_text, Props: Json(props))).Rejection);
    }

    [Fact]
    public void Props_are_normalized_canonically_so_key_order_never_makes_a_different_operation()
    {
        var create = new BoardChange("create", Item: new BoardItemInput(s_new, "block", 0, 0, 200, 120, 0,
            Props: Json("""{"block":{"type":"paragraph","content":[{"type":"text","text":"Ahoj"}]}}""")));
        var set = new BoardChange("set", ItemId: s_text, Props: Json("""{"fontSize":"s","align":"left"}"""));
        var normalized = BoardEditing.Normalize([create, set, Create("note", null)]);
        Assert.Equal("""{"block":{"content":[{"text":"Ahoj","type":"text"}],"type":"paragraph"}}""", Props(normalized[0]));
        Assert.Equal("""{"align":"left","fontSize":"s"}""", Props(normalized[1]));
        // A create without props carries the empty object, as the item is stored.
        Assert.Equal("{}", Props(normalized[2]));
        Assert.Equal(BoardJson.Serialize(normalized), BoardJson.Serialize(BoardEditing.Normalize(BoardJson.Deserialize(BoardJson.Serialize(normalized)))));
    }

    private static BoardEditResult Apply(BoardState state, params BoardChange[] changes) => BoardEditing.Apply(state, changes, 7);

    private static BoardChange Create(string kind, string? props, Guid? target = null)
    {
        return new BoardChange("create", Item: new BoardItemInput(s_new, kind, 0, 0, 200, 120, 0, TargetResourceId: target, Props: props is null ? null : Json(props)));
    }

    private static BoardChange Connector(string props, string? label)
    {
        return new BoardChange("create", Item: new BoardItemInput(s_new, "connector", 0, 0, 8, 8, 0, Text: label, From: s_note, To: s_other, Props: Json(props)));
    }

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Canonical(string json) => BoardJson.Write(BoardProps.Canonical(Json(json)));

    private static string Props(BoardChange change) => BoardJson.Write((change.Props ?? change.Settings ?? change.Item!.Props)!);

    /// <summary>Two notes, a text block (m, center, bold) and a connector between the notes; s_page is readable.</summary>
    private static BoardState State(BoardItemState[]? replaced = null)
    {
        var items = new Dictionary<Guid, BoardItemState>
        {
            [s_note] = new(s_note, "note", 0, 0, 160, 120, 0, "note", "yellow", null, null, null, 1, false),
            [s_other] = new(s_other, "note", 200, 0, 160, 120, 1, null, "green", null, null, null, 1, false),
            [s_text] = new(s_text, "text", 0, 200, 300, 40, 2, "Heading", null, null, null, null, 1, false, """{"align":"center","bold":true,"fontSize":"m"}"""),
            [s_link] = new(s_link, "connector", 0, 0, 8, 8, 3, null, null, null, s_note, s_other, 1, false,
                """{"arrowEnd":"arrow","arrowStart":"none","dash":"solid","route":"elbow"}""")
        };
        foreach (var item in replaced ?? [])
        {
            items[item.Id] = item;
        }
        return new BoardState("Board", items.Values.ToArray(), items.Count, new HashSet<Guid>(), new HashSet<Guid> { s_page });
    }
}
