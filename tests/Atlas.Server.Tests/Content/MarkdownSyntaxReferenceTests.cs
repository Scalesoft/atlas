using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Validation;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// The reference of the Markdown an agent writes (<see cref="MarkdownSyntaxReference"/>, the MCP tool atlas_get_syntax) against the
/// reader and the writer, so its examples cannot go stale and an editor component cannot be added without an entry.
/// </summary>
public sealed class MarkdownSyntaxReferenceTests
{
    private static readonly Guid Page = Guid.Parse("0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b");

    /// <summary>The nodes of the host's own plugins (Atlas.Web), which the editor package's manifest does not know.</summary>
    private static readonly string[] HostNodes = ["overview", "requirement", "requirementSummary"];

    [Fact]
    public void Every_editor_node_has_an_entry_and_every_id_is_unique()
    {
        var covered = MarkdownSyntaxReference.All.SelectMany(x => x.Nodes).ToHashSet(StringComparer.Ordinal);
        // A node of the editor (or of the host) without an entry: add it to MarkdownSyntaxReference.All.
        var missing = MarkdownSchemaManifest.Builtin.NodeNames.Concat(HostNodes).Where(node => !covered.Contains(node)).ToArray();
        Assert.True(missing.Length == 0, "No entry in MarkdownSyntaxReference for: " + string.Join(", ", missing));
        Assert.Equal(MarkdownSyntaxReference.All.Count, MarkdownSyntaxReference.All.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        // An entry says how to write it, or why it cannot be written; a writable one has an example.
        Assert.All(MarkdownSyntaxReference.All, x => Assert.NotEmpty(x.Syntax));
        Assert.All(MarkdownSyntaxReference.All.Where(x => x.Support == SyntaxSupport.Write), x => Assert.NotNull(x.Minimal));
        // The table of components in the MCP guide names every entry.
        var guide = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "mcp-claude-code.md"));
        var undocumented = MarkdownSyntaxReference.All.Where(x => !guide.Contains($"`{x.Id}`", StringComparison.Ordinal)).Select(x => x.Id).ToArray();
        Assert.True(undocumented.Length == 0, "docs/mcp-claude-code.md (Pravidla pro text stránek) lacks: " + string.Join(", ", undocumented));
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    [Fact]
    public void A_writable_example_is_a_valid_document_that_holds_the_component_and_reads_back_unchanged()
    {
        var failures = new List<string>();
        foreach (var component in MarkdownSyntaxReference.All.Where(x => x.Support == SyntaxSupport.Write))
        {
            foreach (var example in new[] { component.Minimal, component.Full }.OfType<SyntaxExample>())
            {
                try
                {
                    var created = RichMarkdownMerge.CreateDocument(example.Text, Context());
                    Assert.True(created.Warnings.Count == 0, "warnings: " + string.Join("; ", created.Warnings));
                    Rules.ValidateDocument(created.ContentJson, created.Markdown);
                    Assert.Equal((example.Stored ?? example.Text).TrimEnd('\n'), created.Markdown.TrimEnd('\n'));
                    // The real component, not a quote or a code block that holds its source.
                    var types = new HashSet<string>(StringComparer.Ordinal);
                    Collect(JsonNode.Parse(created.ContentJson)!["document"], types);
                    var nodes = component.Nodes.Where(node => node is not ("doc" or "text" or "paragraph")).ToArray();
                    Assert.True(nodes.Length == 0 || nodes.Any(types.Contains), "nodes: " + string.Join(", ", types));
                }
                catch (Exception error)
                {
                    failures.Add($"{component.Id}: {error.Message}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    private static void Collect(JsonNode? node, HashSet<string> types)
    {
        if (node?["type"]?.GetValue<string>() is { } type)
        {
            types.Add(type);
        }
        foreach (var child in node?["content"] as JsonArray ?? [])
        {
            Collect(child, types);
        }
    }

    private static MergeContext Context()
    {
        var next = 0;
        return new MergeContext(
            title => title == "Provoz" ? PageLinkResolution.Unique(Page, "Provoz") : PageLinkResolution.None,
            page => page == Page,
            () => "00000000-0000-4000-8000-" + (++next).ToString("D12", System.Globalization.CultureInfo.InvariantCulture));
    }
}
