using System.Text.Json.Nodes;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Xunit;

namespace Atlas.Server.Tests.Projector;

/// <summary>
/// The page's overview card (src/Atlas.Web/src/projector/RequirementSummaryCard.vue, requirementSummaryParity.test.ts) counts what
/// <see cref="RequirementSummary"/>, <see cref="RequirementWaitingBuilder"/> and the blocks' waiting count here: both answer the cases of
/// tests/fixtures/content/requirement-summary.json the same.
/// </summary>
public sealed class RequirementSummaryParityTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Fixture())
        {
            data.Add((string)entry["name"]!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Counts_as_the_web_overview(string name)
    {
        var entry = Fixture().Single(x => (string)x["name"]! == name);
        var blocks = entry["blocks"]!.AsArray().Select((x, position) => new Requirement((string)x!["id"]!, (string)x["key"]!, (string)x["id"]!, (string)x["direction"]!,
            (string)x["state"]!, (string)x["date"]!, (string)x["party"]!, "", x["requires"]!.AsArray().Select(key => (string)key!).ToArray(), (string?)x["parent"], position)).ToArray();
        var expected = entry["summary"]!;
        var summary = RequirementSummary.Of(blocks);
        Assert.Equal((int)expected["total"]!, summary.Total);
        Assert.Equal((int)expected["applicable"]!, summary.Applicable);
        Assert.Equal((int)expected["done"]!, summary.Done);
        Assert.Equal((int)expected["percent"]!, summary.Percent);
        Assert.Equal((int)expected["waiting"]!, summary.Waiting);
        Assert.Equal(expected["inboundMissing"]!.AsArray().Select(x => (string)x!), summary.InboundMissing.Select(x => x.BlockId));
        Assert.Equal(expected["states"]!.AsObject().ToDictionary(x => x.Key, x => (int)x.Value!), summary.States.ToDictionary(x => x.Key, x => x.Value));

        var waitingOn = RequirementSummary.WithWaitingOn(blocks).Where(x => x.WaitingOn.Count > 0).ToDictionary(x => x.BlockId, x => x.WaitingOn.ToArray());
        Assert.Equal(entry["waitingOn"]!.AsObject().ToDictionary(x => x.Key, x => x.Value!.AsArray().Select(key => (string)key!).ToArray()), waitingOn);

        var page = Guid.NewGuid();
        var parties = RequirementWaitingBuilder.Build(blocks.Where(x => x.State == "waiting_for_input").Select(x => new RequirementRow(page, "Stránka", x)).ToArray()).Parties;
        Assert.Equal(entry["parties"]!.AsArray().Select(x => ((string)x![0]!, string.Join(",", x[1]!.AsArray().Select(id => (string)id!)))),
            parties.Select(x => (x.Party, string.Join(",", x.Items.Select(item => item.BlockId)))));
    }

    private static IEnumerable<JsonNode> Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "requirement-summary.json")))!.AsArray().Select(x => x!);
}
