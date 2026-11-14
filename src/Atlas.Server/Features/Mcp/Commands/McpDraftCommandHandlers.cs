using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Domain;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Platform.Content;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Commands;

// The draft writes of MCP (plan C2). Each runs the shared handler of the web and REST write (create, save, rename) inside its own
// transaction, so the channel mcp and the token come from IRequestChannel, the author is the token owner, the live-edit guard of D3
// refuses even the owner's own unsaved edits, the CAS on the expected version, password references and attachment bindings are the
// handler's. MCP adds only G5 in the same transaction (an excluded space is the 404 of a missing page) and a version conflict that
// names the current version to an editor. Nothing here publishes: readers keep seeing the published revision.

public sealed class CreateMcpDraftHandler
{
    private readonly McpVisibility m_visibility;
    private readonly CreatePageHandler m_create;
    private readonly ContactLinker m_contacts;

    public CreateMcpDraftHandler(McpVisibility visibility, CreatePageHandler create, ContactLinker contacts)
    {
        m_visibility = visibility;
        m_create = create;
        m_contacts = contacts;
    }

    public async Task<McpWriteResult> Handle(CreateMcpDraft command, CancellationToken ct)
    {
        // The create's own locks: LockActorAsync alone would hold the space FOR SHARE, which the insert then has to upgrade.
        await m_create.LockSpaceAsync(command.Request.SpaceId, ct);
        try
        {
            await m_visibility.RequireSpaceAsync(command.Request.SpaceId, ct);
        }
        catch (ApiException)
        {
            throw McpWriteErrors.MissingSpace();
        }
        // The text was converted before this transaction (errors come before any write), also when appended to a template, which the
        // create copies only when it is still the template that was read. The personal API shares the same create.
        var page = await m_create.Handle(new CreatePage(command.Request, FromMarkdown: true, command.TemplateContentJson, Link: command.Link), ct);
        return new McpWriteResult(page.Id, page.SpaceId, page.Version, [], m_contacts.Last, page.Links, page.Decoration);
    }
}

public sealed class SaveMcpDraftHandler
{
    private readonly ISession m_session;
    private readonly McpOrganizationGuard m_guard;
    private readonly SavePageHandler m_save;
    private readonly ContactLinker m_contacts;

    public SaveMcpDraftHandler(ISession session, McpOrganizationGuard guard, SavePageHandler save, ContactLinker contacts)
    {
        m_session = session;
        m_guard = guard;
        m_save = save;
        m_contacts = contacts;
    }

    public async Task<McpWriteResult> Handle(SaveMcpDraft command, CancellationToken ct)
    {
        // G5 under the space lock (content shared, space, G5), so a switch out of MCP cannot interleave with the write.
        await m_guard.LockPageSpaceSharedAsync(command.PageId, ct);
        PageDto page;
        try
        {
            page = await m_save.Handle(new SavePage(command.PageId, command.Request, FromMarkdown: true, command.Link), ct);
        }
        catch (ApiException error) when (error.Code == "version_conflict")
        {
            // The save decided EditContent before the version, and holds the page row; the version it saw is the current one.
            throw McpWriteErrors.VersionConflict((await m_session.GetAsync<Page>(command.PageId, ct))!.Version);
        }
        return new McpWriteResult(page.Id, page.SpaceId, page.Version, [], m_contacts.Last, page.Links);
    }
}

public sealed class RenameMcpPageHandler
{
    private readonly ISession m_session;
    private readonly McpOrganizationGuard m_guard;
    private readonly RenamePageHandler m_rename;

    public RenameMcpPageHandler(ISession session, McpOrganizationGuard guard, RenamePageHandler rename)
    {
        m_session = session;
        m_guard = guard;
        m_rename = rename;
    }

    public async Task<McpWriteResult> Handle(RenameMcpPage command, CancellationToken ct)
    {
        // The rename takes the space FOR UPDATE: take it up front, as the organization guard does, before G5.
        await m_guard.LockPageSpaceAsync(command.PageId, ct);
        try
        {
            await m_rename.Handle(new RenamePage(command.PageId, command.Request), ct);
        }
        catch (ApiException error) when (error.Code == "version_conflict")
        {
            throw McpWriteErrors.VersionConflict((await m_session.GetAsync<Page>(command.PageId, ct))!.Version);
        }
        var page = (await m_session.GetAsync<Page>(command.PageId, ct))!;
        return new McpWriteResult(page.Id, page.SpaceId, page.Version, []);
    }
}
