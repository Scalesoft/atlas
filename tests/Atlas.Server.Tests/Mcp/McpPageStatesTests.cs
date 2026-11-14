using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Tests.Platform;
using Atlas.Markdown.Sync;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

public sealed class McpPageStatesTests
{
    /// <summary>
    /// atlas_list_pages passes only ids its tree listed, but the query answers any id of the space. A state row says that the
    /// page exists and whether someone is typing in it, so the query applies the list draft rule itself: a Writer edits another
    /// author's never-published draft by role and still does not see it in any list.
    /// </summary>
    [PostgreSqlFact]
    public async Task States_leave_out_another_authors_unpublished_draft_as_the_lists_do()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_mcp_states_");
        var author = await host.SeedUserAsync("author");
        var writer = await host.SeedUserAsync("writer");
        var space = await host.SeedSpaceAsync("Drafts", (author, SpaceRole.Writer), (writer, SpaceRole.Writer));
        host.Actor.Current = author;
        var draft = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(space, null, "Private", McpSite.Rich(McpSite.Paragraph("Private")), "Private")));
        var shared = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(space, null, "Shared", McpSite.Rich(McpSite.Paragraph("Shared")), "Shared")));
        await host.InvokeAsync<PageDto>(new PublishPage(shared.Id, new PublishPageRequest(shared.Version)));
        var live = await host.InvokeAsync<WikiCollaborationSession>(new OpenCollaboration(draft.Id));
        await host.InvokeAsync<AppendUpdateResult>(new AppendUpdate(draft.Id, live.Generation, Guid.NewGuid(), author.Id, DateTimeOffset.UtcNow, [1]));

        host.Actor.Current = writer;
        var others = await host.InvokeAsync<McpPageStates>(new GetMcpPageStates(space, [draft.Id, shared.Id]));
        host.Actor.Current = author;
        var own = await host.InvokeAsync<McpPageStates>(new GetMcpPageStates(space, [draft.Id, shared.Id]));

        Assert.Equal([new McpPageState(shared.Id, false, false)], others.Items);
        Assert.Equal([new McpPageState(draft.Id, false, true), new McpPageState(shared.Id, false, false)], own.Items.OrderBy(state => state.PageId == shared.Id));
    }
}
