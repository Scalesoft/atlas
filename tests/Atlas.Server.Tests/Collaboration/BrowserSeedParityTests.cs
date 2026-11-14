using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Platform.Validation;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>
/// The server's seed of a stored page is the browser's: <see cref="BrowserSeed.Prepare"/> of every seed fixture's stored <c>input</c>,
/// seeded and read back, is the document the JS seed (the Node authority's seedRich, its plugins' appended transactions included) left,
/// so an editor opening a server-seeded page has nothing to normalize and makes no edit of its own. Fixtures:
/// tests/Atlas.Yjs.Tests/Fixtures/yjs-writer/cases.json.gz (src/Atlas.Web/src/server/yjsWriterFixtures.test.ts).
/// </summary>
public sealed partial class BrowserSeedParityTests
{
    private static readonly Lazy<Dictionary<string, (uint ClientId, string Input, string State)>> Seeds = new(Load);

    private static readonly YjsDocumentWriter Writer = new(HostDocumentValidator.Instance.Schema);

    private static readonly YjsDocumentMaterializer Materializer = new(HostDocumentValidator.Instance.Schema);

    public static TheoryData<string> Names => new(Seeds.Value.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public void The_server_seed_of_a_stored_page_is_the_browsers(string name)
    {
        var (clientId, input, state) = Seeds.Value[name];
        using var prepared = JsonDocument.Parse(BrowserSeed.Prepare(JsonNode.Parse(input)!.AsObject()).ToJsonString());

        var seeded = Materializer.Materialize(Writer.Seed(prepared.RootElement, clientId));

        Assert.Equal(Masked(Materializer.Materialize(Convert.FromBase64String(state)), input), Masked(seeded, input));
    }

    /// <summary>The ids a seed assigns at random (a table part's chart reference, a repeated heading's id) are equal where both assigned
    /// one: a UUID the stored page does not hold.</summary>
    private static string Masked(string json, string input) => Uuid().Replace(json, match => input.Contains(match.Value, StringComparison.Ordinal) ? match.Value : "*");

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex Uuid();

    private static Dictionary<string, (uint, string, string)> Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Atlas.slnx")))
        {
            directory = directory.Parent;
        }
        var path = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Atlas.slnx not found above " + AppContext.BaseDirectory),
            "tests", "Atlas.Yjs.Tests", "Fixtures", "yjs-writer", "cases.json.gz");
        using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip, new JsonDocumentOptions { MaxDepth = 256 });
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == "seed")
            .ToDictionary(entry => entry.GetProperty("name").GetString()!,
                entry => (entry.GetProperty("clientId").GetUInt32(), entry.GetProperty("input").GetRawText(), entry.GetProperty("state").GetString()!),
                StringComparer.Ordinal);
    }
}
