using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>
/// The embed tool's write, as <see cref="SaveMcpDraftHandler"/>: G5 in the same transaction under the space lock (an excluded space is the 404 of a missing
/// page), then the shared <see cref="EditPageEmbedHandler"/>, which checks the attachment under the page lock and saves through the
/// shared save (channel mcp, the token, the live-edit rule that keeps even the owner's unsaved edits). A version conflict names the
/// current version; a page the actor may read but not edit is the 404 of the working revision it may not see.
/// </summary>
public sealed class SaveMcpEmbedHandler
{
    private readonly ISession m_session;
    private readonly McpOrganizationGuard m_guard;
    private readonly EditPageEmbedHandler m_embeds;

    public SaveMcpEmbedHandler(ISession session, McpOrganizationGuard guard, EditPageEmbedHandler embeds)
    {
        m_session = session;
        m_guard = guard;
        m_embeds = embeds;
    }

    public async Task<McpWriteResult> Handle(SaveMcpEmbed command, CancellationToken ct)
    {
        await m_guard.LockPageSpaceSharedAsync(command.Edit.PageId, ct);
        try
        {
            var page = await m_embeds.ApplyAsync(command.Edit, ct);
            return new McpWriteResult(page.Id, page.SpaceId, page.Version, []);
        }
        catch (ApiException error) when (error.Code == "version_conflict")
        {
            throw McpWriteErrors.VersionConflict((await m_session.GetAsync<Page>(command.Edit.PageId, ct))!.Version);
        }
    }
}
