using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

public sealed class McpDraftConcurrencyTests
{
    [PostgreSqlFact]
    public async Task Concurrent_draft_creates_in_one_space_all_succeed()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_mcp_create_");
        var writer = await host.SeedUserAsync("writer");
        var space = await host.SeedSpaceAsync("Drafts", (writer, SpaceRole.Writer));
        host.Actor.Current = writer;
        // atlas_create_draft decides G5 under its own locks before the shared create, so it has to take the space as the create does.
        var created = await Task.WhenAll(Enumerable.Range(1, 6).Select(index => Task.Run(() => host.InvokeAsync<McpWriteResult>(
            new CreateMcpDraft(new(space, null, "Draft " + index, McpSite.Rich(McpSite.Paragraph("Text")), "Text"))))));
        Assert.Equal(6, created.Select(result => result.PageId).Distinct().Count());
    }
}
