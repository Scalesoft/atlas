using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Tests.Mcp;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>
/// 3.6: the document authority in managed code (byte parity of its writer with the JS one: tests/Atlas.Yjs.Tests) merges a live write with a
/// person typing at the same time without loss or duplication, seeds as the browser does, and refuses a target the editor would not read.
/// </summary>
public sealed class ManagedLiveWriteAuthorityTests
{
    private static readonly MarkdownDocumentSchema Schema = CollaborationSchema.KnownHostSchemas[^1];

    private static readonly YjsDocumentMaterializer Host = new(HostDocumentValidator.Instance.Schema);

    /// <summary>The page of <see cref="McpLiveWriteTests.Baseline"/> with section B rewritten by the agent.</summary>
    private static JsonElement Target()
    {
        var json = Host.Materialize(McpLiveWriteTests.Baseline).Replace("\"Text B.\"", "\"Text B přepsal agent.\"", StringComparison.Ordinal);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task A_person_typing_while_the_agent_writes_keeps_both_once_in_either_order()
    {
        var authority = new ManagedLiveWriteAuthority();
        var update = await authority.WriteAsync(Schema, McpLiveWriteTests.Baseline, [], Target(), 1, CancellationToken.None);

        var personFirst = Host.Materialize(McpLiveWriteTests.Baseline, [McpLiveWriteTests.Human, update]);
        var agentFirst = Host.Materialize(McpLiveWriteTests.Baseline, [update, McpLiveWriteTests.Human]);
        Assert.Equal(personFirst, agentFirst);
        Assert.Single(personFirst.Split("Text A. Píše člověk.")[1..]);
        Assert.Single(personFirst.Split("Text B přepsal agent.")[1..]);
        var checkpoint = await authority.CheckpointAsync(Schema, McpLiveWriteTests.Baseline, [McpLiveWriteTests.Human, update], 2, CancellationToken.None);
        Assert.Equal(personFirst, checkpoint.Projections.EditorJson.GetRawText());
        Assert.Contains("Text A. Píše člověk.", checkpoint.Projections.Markdown);
        Assert.Contains("Text B přepsal agent.", checkpoint.Projections.Markdown);
        Assert.Equal(2, checkpoint.CoveredSequence);
    }

    /// <summary>
    /// Campaign 2026-11-05: the person types into the very paragraph the agent rewrites, and the agent's update was built from a state
    /// without the person's keystrokes (they reach the relay between its read and its append). Both land once, in either order.
    /// </summary>
    [Theory]
    [InlineData("Text B. Píše člověk.", "Text B přepsal agent.")]
    [InlineData("Píše člověk. Text B.", "Text B přepsal agent.")]
    [InlineData("Text Píše člověk. B.", "Úplně nový odstavec agenta.")]
    [InlineData("Text B. Píše člověk.", "Úplně nový odstavec agenta.")]
    public async Task A_person_typing_into_the_paragraph_the_agent_rewrites_keeps_both_once_in_either_order(string person, string agent)
    {
        var baseline = Host.Materialize(McpLiveWriteTests.Baseline);
        using var personTarget = JsonDocument.Parse(baseline.Replace("\"Text B.\"", JsonSerializer.Serialize(person), StringComparison.Ordinal));
        using var agentTarget = JsonDocument.Parse(baseline.Replace("\"Text B.\"", JsonSerializer.Serialize(agent), StringComparison.Ordinal));
        var typed = new YjsDocumentWriter(HostDocumentValidator.Instance.Schema).Write(McpLiveWriteTests.Baseline, [], personTarget.RootElement, 4711).Update;
        var authority = new ManagedLiveWriteAuthority();
        var update = await authority.WriteAsync(Schema, McpLiveWriteTests.Baseline, [], agentTarget.RootElement, 1, CancellationToken.None);

        var personFirst = Host.Materialize(McpLiveWriteTests.Baseline, [typed, update]);
        var agentFirst = Host.Materialize(McpLiveWriteTests.Baseline, [update, typed]);
        Assert.Equal(personFirst, agentFirst);
        var checkpoint = await authority.CheckpointAsync(Schema, McpLiveWriteTests.Baseline, [typed, update], 2, CancellationToken.None);
        var text = checkpoint.Projections.Markdown;
        Assert.Single(text.Split("Píše člověk.")[1..]);
        // The agent's words land as written, whole; of the old paragraph only what both kept stays.
        Assert.Single(text.Split(agent.TrimEnd('.'))[1..]);
        Assert.Contains("Text A.", text);
    }

    [Fact]
    public async Task The_seed_is_the_browsers_a_trailing_paragraph_a_chart_id_only_once_and_dependencies_only_under_a_requirement()
    {
        var stored = JsonNode.Parse("""
            {"type":"doc","content":[
              {"type":"paragraph","attrs":{"dependencies":true},"content":[{"type":"text","text":", "},{"type":"text","text":"bez nadpisu"}]},
              {"type":"table","content":[{"type":"tableRow","content":[{"type":"tableCell","attrs":{"chartRef":"chart-a"},"content":[{"type":"paragraph"}]},
                {"type":"tableCell","attrs":{"chartRef":"chart-a"},"content":[{"type":"paragraph"}]}]}]},
              {"type":"heading","attrs":{"level":2,"headingId":"r","state":"planned"},"content":[{"type":"text","text":"Požadavek"}]},
              {"type":"paragraph","attrs":{"dependencies":true},"content":[{"type":"pageReference","attrs":{"id":"5e940f4d-55c2-4527-addd-10f1d68d742c",
                "title":"Jiný","url":"#/pages/5e940f4d-55c2-4527-addd-10f1d68d742c?heading=a"}}]},
              {"type":"heading","attrs":{"level":2,"headingId":"z"},"content":[{"type":"text","text":"Závěr"}]}]}
            """)!.AsObject();

        var seed = BrowserSeed.Prepare(stored);

        var content = seed["content"]!.AsArray();
        Assert.Equal("paragraph", content[^1]!["type"]!.GetValue<string>());
        Assert.Null(content[^1]!["content"]);
        Assert.False(content[0]!["attrs"]!["dependencies"]!.GetValue<bool>());
        Assert.Equal("bez nadpisu", Assert.Single(content[0]!["content"]!.AsArray())!["text"]!.GetValue<string>());
        Assert.True(content[3]!["attrs"]!["dependencies"]!.GetValue<bool>());
        // A chart id the page holds stays, once (a repeated one is dropped); a table part without one gets none.
        var row = content[1]!["content"]![0]!;
        Assert.Null(content[1]!["attrs"]?["chartRef"]);
        Assert.Null(row["attrs"]?["chartRef"]);
        Assert.Equal("chart-a", row["content"]![0]!["attrs"]!["chartRef"]!.GetValue<string>());
        Assert.Null(row["content"]![1]!["attrs"]!["chartRef"]);
        var snapshot = await new ManagedLiveWriteAuthority().SeedAsync(Schema, JsonDocument.Parse(stored.ToJsonString()).RootElement, CancellationToken.None);
        Assert.Equal(0, snapshot.CoveredSequence);
        Assert.Contains("Požadavek", snapshot.Projections.Markdown);
    }

    [Fact]
    public async Task A_request_whose_caller_gave_up_builds_nothing()
    {
        var authority = new ManagedLiveWriteAuthority();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var document = JsonDocument.Parse("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Text."}]}]}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authority.SeedAsync(Schema, document.RootElement, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authority.WriteAsync(Schema, McpLiveWriteTests.Baseline, [], Target(), 1, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authority.CheckpointAsync(Schema, McpLiveWriteTests.Baseline, [McpLiveWriteTests.Human], 1, cancelled.Token));
    }

    [Fact]
    public async Task A_target_the_schema_does_not_know_writes_nothing()
    {
        var authority = new ManagedLiveWriteAuthority();
        using var script = JsonDocument.Parse("""{"type":"doc","content":[{"type":"script"}]}""");

        await Assert.ThrowsAnyAsync<Exception>(() => authority.WriteAsync(Schema, McpLiveWriteTests.Baseline, [], script.RootElement, 1, CancellationToken.None));
    }
}
