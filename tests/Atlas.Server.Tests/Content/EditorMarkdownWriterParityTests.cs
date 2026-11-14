using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// Pins what <see cref="EditorMarkdownWriter"/> writes for documents holding every text node and mark of the editor
/// (tests/fixtures/content/text-nodes.json) and every Atlas component (components.json) to tests/fixtures/content/expected.
/// mark-order.json holds texts whose marks close in another order than they opened, where the editor's serializer relies on
/// <c>Array.prototype.slice</c> clamping its bounds: a mark that outlives two marks opened before it (the smallest such
/// document), the same after an earlier text reordered the open marks, and one where the clamped slice moves a mark behind
/// the rest, which changes the order the marks open in.
/// The web project's <c>src/wikiImportGolden.test.ts</c> opens the same files in the editor pinned in 2.0 and checks that
/// the editor writes exactly this Markdown for the same JSON; that is the parity check, this test only keeps the files current.
/// Regenerate after an intended writer change with <c>ATLAS_UPDATE_GOLDEN=1 dotnet test --filter EditorMarkdownWriterParityTests</c>.
/// </summary>
public sealed class EditorMarkdownWriterParityTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Theory]
    [InlineData("text-nodes.json")]
    [InlineData("components.json")]
    [InlineData("mark-order.json")]
    // Projector's requirement blocks and summary (spec 2026-10-27 §2.3): the web writes them in src/projector/requirementNode.ts.
    [InlineData("requirements.json")]
    public async Task Server_writer_matches_the_golden_file_the_editor_checks(string file)
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(Fixture(file)))!.AsArray();
        var golden = new JsonArray(input.Select(item => (JsonNode)new JsonObject
        {
            ["name"] = item!["name"]!.GetValue<string>(),
            ["content"] = item["content"]!.DeepClone(),
            ["markdown"] = EditorMarkdownWriter.Write(item["content"]!["document"]!.AsObject()),
        }).ToArray());
        await AssertGoldenAsync(file, golden);
    }

    /// <summary>
    /// The other bound of the reordering step mark-order.json covers: the mark is found at the open position right behind the
    /// node's own marks, in a text that ends in the code mark. The editor refuses such a document (its code mark excludes
    /// every other mark), so no golden file can hold it, but a page stored through the API can, and reading it must not fail.
    /// Expected is what the pinned serializer code (prosemirror-markdown under tiptap-markdown, the editor's mark specs) writes
    /// when it runs on a schema without that exclusion: the bold is lost and the code opens twice.
    /// </summary>
    [Fact]
    public void A_mark_reordered_past_a_trailing_code_mark_is_written_as_the_serializer_writes_it()
    {
        var document = JsonNode.Parse("""
            { "type": "doc", "content": [{ "type": "paragraph", "content": [
              { "type": "text", "text": "a", "marks": [{ "type": "italic" }, { "type": "highlight" }] },
              { "type": "text", "text": "b", "marks": [{ "type": "bold" }, { "type": "italic" }, { "type": "highlight" }] },
              { "type": "text", "text": "c", "marks": [{ "type": "bold" }, { "type": "code" }] }
            ] }] }
            """)!.AsObject();

        Assert.Equal("*==a**b**==*``c``", EditorMarkdownWriter.Write(document));
    }

    /// <summary>
    /// Compares <paramref name="golden"/> with tests/fixtures/content/expected/<paramref name="file"/>, or rewrites that file
    /// under <c>ATLAS_UPDATE_GOLDEN=1</c>. The web golden test opens every file there in the editor.
    /// </summary>
    internal static async Task AssertGoldenAsync(string file, JsonArray golden)
    {
        var actual = golden.ToJsonString(Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        if (Environment.GetEnvironmentVariable("ATLAS_UPDATE_GOLDEN") == "1")
        {
            var target = Path.Combine(SourceDirectory(), "..", "..", "fixtures", "content", "expected");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, file), actual, new UTF8Encoding(false));
            return;
        }
        var path = Fixture(Path.Combine("expected", file));
        Assert.True(File.Exists(path), "Missing golden file " + path + "; generate it with ATLAS_UPDATE_GOLDEN=1.");
        Assert.Equal((await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }

    internal static string Fixture(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "fixtures", "content", name);
    }

    private static string SourceDirectory([CallerFilePath] string path = "")
    {
        return Path.GetDirectoryName(path)!;
    }
}
