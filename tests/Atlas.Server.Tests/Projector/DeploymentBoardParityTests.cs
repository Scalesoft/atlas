using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Xunit;

namespace Atlas.Server.Tests.Projector;

/// <summary>
/// projector_compose_board lays out the board as the web's „Poskládat tabuli“ (src/Atlas.Web/src/projector/deploymentMap.ts,
/// deploymentBoardParity.test.ts): <see cref="DeploymentBoard"/> answers the cases of tests/fixtures/content/deployment-board.json with the
/// same changes, ids numbered in the order they are made.
/// </summary>
public sealed class DeploymentBoardParityTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Fixture()["cases"]!.AsArray())
        {
            data.Add((string)entry!["name"]!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Composes_as_the_web(string name) => Composes("document", "cases", name);

    /// <summary>A converted page (requirement headings) with one legacy block left under a plain heading: both shapes are read.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Composes_a_heading_page_as_the_web(string name) => Composes("headingDocument", "headingCases", name);

    /// <summary>An order's page: its own section first, an internal integration, a shared service; a board a person laid out gets only new cards below.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Composes_an_order_page_as_the_web(string name) => Composes("orderDocument", "orderCases", name);

    public static TheoryData<string> ArrangeCases()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Fixture()["arrangeCases"]!.AsArray())
        {
            data.Add((string)entry!["name"]!);
        }
        return data;
    }

    /// <summary>„Uspořádat“ (deploymentMap.ts arrangeChanges): the same moves and connector sides, a null side included, in the same order.</summary>
    [Theory]
    [MemberData(nameof(ArrangeCases))]
    public void Arranges_as_the_web(string name)
    {
        var fixture = Fixture();
        var entry = fixture["arrangeCases"]!.AsArray().Single(x => (string)x!["name"]! == name)!;
        var pages = entry["pages"]!.AsArray().Select(page => (Guid.Parse((string)page!["pageId"]!), DeploymentBoard.Of(fixture[(string)page["document"]!]))).ToList();
        var ids = entry["ids"]?.AsArray().Select(id => Guid.Parse((string)id!)).ToList();
        var computed = JsonSerializer.SerializeToNode(DeploymentBoard.Arrange(pages, Items(entry), ids), Web)!;
        Assert.True(JsonNode.DeepEquals(entry["changes"], computed), computed.ToJsonString());
    }

    private static List<BoardItemDto> Items(JsonNode entry) => entry["items"]!.AsArray().Select(x => new BoardItemDto(Guid.Parse((string)x!["id"]!), (string)x["kind"]!, (double)x["x"]!, (double)x["y"]!,
        (double)x["width"]!, (double)x["height"]!, (int)x["z"]!, (string?)x["text"], null, JsonSerializer.SerializeToElement(x["props"] ?? new JsonObject()),
        x["from"] is { } from ? Guid.Parse((string)from!) : null, x["to"] is { } to ? Guid.Parse((string)to!) : null, 1,
        x["target"] is { } target ? new BoardTargetDto("page", Guid.Parse((string)target["resourceId"]!), "", null, null, true) : null)).ToList();

    private static void Composes(string document, string cases, string name)
    {
        var fixture = Fixture();
        var entry = fixture[cases]!.AsArray().Single(x => (string)x!["name"]! == name)!;
        var items = Items(entry);
        var next = 0;
        var changes = DeploymentBoard.Compose(DeploymentBoard.Of(fixture[document]), Guid.Parse((string)fixture["pageId"]!), items,
            () => Guid.Parse($"00000000-0000-4000-8000-{++next:D12}"));
        var computed = JsonSerializer.SerializeToNode(changes, Web)!;
        Assert.True(JsonNode.DeepEquals(entry["changes"], computed), computed.ToJsonString());
    }

    /// <summary>deploymentMap.test.ts „what a board accepts“ says the same of the web.</summary>
    [Fact]
    public void A_temporary_heading_id_is_no_anchor_and_a_label_fits_a_board_item()
    {
        var document = JsonNode.Parse("""
            {"type":"doc","content":[{"type":"heading","attrs":{"level":2,"headingId":"v3-h1"},"content":[{"type":"text","text":"Portál"}]},
              {"type":"requirement","attrs":{"id":"b1"},"content":[{"type":"paragraph","content":[{"type":"text","text":"Server"}]}]}]}
            """);
        Assert.Equal("portal", DeploymentBoard.Of(document).Products.Single().Anchor);
        Assert.Equal(4000, DeploymentBoard.LinkLabel(new DeploymentBoard.Link("p", "s", [new string('m', 5000)], 1, "planned")).Length);
    }

    /// <summary>deploymentMap.test.ts „makes a product card tall enough…“ says the same of the web: long tags take a row each, short ones share one.</summary>
    [Fact]
    public void A_product_card_is_as_tall_as_the_rows_its_tags_wrap_into()
    {
        var tags = Enumerable.Range(0, 13).Select(n => new DeploymentBoard.Block($"b{n}", $"Interní URL a DNS záznam MPI číslo {n}", null)).ToList();
        Assert.Equal(13, DeploymentBoard.TagRows(tags));
        Assert.Equal(1, DeploymentBoard.TagRows(new[] { "DNS", "SSL", "RDP" }.Select(title => new DeploymentBoard.Block(title, title, null))));
        var mpi = new DeploymentBoard.Product("MPI", "mpi", tags, [new("Integrace", [new("MPI — ÚZIS", "uzis", [])])], tags);
        Assert.Equal(84 + 14 + 26 * 13 + 44 + 36, DeploymentBoard.ProductHeight(mpi));
    }

    private static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "deployment-board.json")))!.AsObject();
}
