using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;

namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>atlas_set_publication: the shared write of the page's „Publikace“ after G5 (3.6-I).</summary>
public sealed record SetMcpPublication(Guid PageId, SetPagePublicationRequest Request);

public sealed class SetMcpPublicationHandler(McpOrganizationGuard guard, PagePublicationSettingHandler handler)
{
    public async Task<PagePublicationState> Handle(SetMcpPublication command, CancellationToken ct)
    {
        await guard.LockPageSpaceAsync(command.PageId, ct);
        // A readable page without the right to publish answers the shared 403, as REST does: the gate says 404 only for a page the actor may not see.
        return await handler.Handle(new SetPagePublication(command.PageId, command.Request), ct);
    }
}
