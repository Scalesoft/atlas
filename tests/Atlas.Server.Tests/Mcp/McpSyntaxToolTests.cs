using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Tools;
using Atlas.Server.Platform.Content;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

/// <summary>atlas_get_syntax: the overview, one component, an unknown name, and the server's instructions that point to it.</summary>
public sealed class McpSyntaxToolTests
{
    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public void The_tool_answers_the_overview_one_component_and_the_valid_names_for_an_unknown_one()
    {
        var tools = new McpSyntaxTools();

        var overview = tools.GetSyntax();
        Assert.True(overview.IsError != true);
        foreach (var component in MarkdownSyntaxReference.All)
        {
            Assert.Contains(component.Id + ":", Text(overview));
        }
        Assert.Equal(MarkdownSyntaxReference.All.Count, JsonNode.Parse(overview.StructuredContent!.Value.GetRawText())!["components"]!.AsArray().Count);

        var callout = tools.GetSyntax(" CALLOUT ");
        var text = Text(callout);
        Assert.Contains("> [!WARNING]\n> ", text);
        Assert.Contains("Minimální příklad", text);
        Assert.Contains("Časté chyby", text);
        Assert.Equal("callout", JsonNode.Parse(callout.StructuredContent!.Value.GetRawText())!["component"]!.GetValue<string>());
        Assert.Contains("nelze", Text(tools.GetSyntax("toc")));

        var unknown = tools.GetSyntax("infobox");
        Assert.True(unknown.IsError);
        foreach (var component in MarkdownSyntaxReference.All)
        {
            Assert.Contains(component.Id, Text(unknown));
        }

        // The server's instructions point to the tool and show the callout form.
        Assert.Contains(McpSyntaxTools.ToolName, McpModule.Instructions);
        Assert.Contains("[!WARNING]", McpModule.Instructions);
        Assert.Contains(MarkdownSyntaxReference.WritableSummary, McpModule.Instructions);
    }
}
