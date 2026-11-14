using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Atlas.Server.Tests.ContentImport;

/// <summary>
/// Pins the complete conversion of each fixture wiki under tests/fixtures/wiki-import (editor JSON,
/// Markdown projection and findings of every page) to tests/fixtures/wiki-import/expected/&lt;wiki&gt;.json.
/// Any difference fails. After an intended converter change regenerate the files with
/// <c>ATLAS_UPDATE_GOLDEN=1 dotnet test --filter WikiImportGoldenTests</c> and review the diff.
/// The web project's <c>src/wikiImportGolden.test.ts</c> opens the same files in the Atlas editor and
/// checks that the editor keeps every document unchanged and writes the same Markdown projection,
/// which catches editor schema or serializer drift on an editor upgrade.
/// </summary>
public sealed class WikiImportGoldenTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static TheoryData<string> Wikis => new() { "github", "obsidian", "wikijs", "constructs" };

    [Trait("Category", "Smoke")]
    [Theory]
    [MemberData(nameof(Wikis))]
    public async Task Converted_fixture_wiki_matches_its_golden_file(string wiki)
    {
        var pages = await ImportPages.FixtureAsync(wiki, wiki == "wikijs" ? "https://wiki.example.com/en" : null);
        var golden = new JsonArray(pages.OrderBy(x => x.Source.Path, StringComparer.Ordinal).Select(page => (JsonNode)new JsonObject
        {
            ["path"] = page.Source.Path,
            ["title"] = page.Source.Title,
            ["tags"] = new JsonArray(page.Source.Tags.Select(x => (JsonNode)x).ToArray()),
            ["content"] = JsonNode.Parse(page.Document.ContentJson),
            ["markdown"] = page.Document.Markdown,
            ["findings"] = new JsonArray(page.Document.Findings.Select(x => (JsonNode)new JsonObject
            {
                ["construct"] = x.Construct, ["impact"] = x.Impact, ["count"] = x.Count, ["line"] = x.Line, ["excerpt"] = x.Excerpt,
            }).ToArray()),
        }).ToArray());
        var actual = golden.ToJsonString(Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        if (Environment.GetEnvironmentVariable("ATLAS_UPDATE_GOLDEN") == "1")
        {
            var target = Path.Combine(SourceDirectory(), "..", "..", "fixtures", "wiki-import", "expected");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, wiki + ".json"), actual, new UTF8Encoding(false));
            return;
        }
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "wiki-import", "expected", wiki + ".json");
        Assert.True(File.Exists(path), "Missing golden file " + path + "; generate it with ATLAS_UPDATE_GOLDEN=1.");
        Assert.Equal((await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }

    private static string SourceDirectory([CallerFilePath] string path = "")
    {
        return Path.GetDirectoryName(path)!;
    }
}
