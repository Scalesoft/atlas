using System.Text.Json.Nodes;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Xunit;

namespace Atlas.Server.Tests.Projector;

/// <summary>
/// The catalog panel resolves on the client (src/Atlas.Web/src/projector/requirementResolver.ts) and the server checks every write with
/// <see cref="RequirementResolver"/>; both answer the cases of tests/fixtures/content/requirement-resolver.json the same.
/// </summary>
public sealed class RequirementResolverParityTests
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
    public void Answers_as_the_web_resolver(string name)
    {
        var entry = Fixture().Single(x => (string)x["name"]! == name);
        var catalog = entry["catalog"]!.AsArray().Select(x => new CatalogDefinition((string)x!["key"]!, (string)x["title"]!, (string)x["title"]!, "internal",
            x["requires"]!.AsArray().Select(key => (string)key!).ToArray(), new JsonObject { ["type"] = "requirement" }, Guid.Empty, "", "")).ToArray();
        var keys = (string field) => entry[field]!.AsArray().Select(x => (string)x!).ToArray();
        if (entry["error"] is JsonObject error)
        {
            var refused = Assert.Throws<ApiException>(() => RequirementResolver.Resolve(catalog, keys("wanted"), keys("present"), keys("notApplicable")));
            Assert.Equal((string)error["code"]!, refused.Code);
            if (error["path"] is JsonArray path)
            {
                Assert.Equal(path.Select(x => (string)x!), (IEnumerable<string>)refused.Extensions!["path"]!);
            }
            return;
        }
        var resolution = RequirementResolver.Resolve(catalog, keys("wanted"), keys("present"), keys("notApplicable"));
        Assert.Equal(entry["items"]!.AsArray().Select(x => ((string)x!["key"]!, (string?)x["requiredBy"], (string)x["reason"]!)),
            resolution.Items.Select(x => (x.Definition.Key, x.RequiredBy, x.Reason)));
        Assert.Equal(keys("warnings"), resolution.Warnings);
    }

    private static IEnumerable<JsonNode> Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "requirement-resolver.json")))!.AsArray().Select(x => x!);
}
