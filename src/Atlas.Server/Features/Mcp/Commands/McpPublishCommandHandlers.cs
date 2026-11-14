using Microsoft.AspNetCore.Authorization;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Publishing;
using Atlas.Server.Features.PageHistory.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.PersonalApi;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Commands;

// Publication over MCP (3.6): a token with the scope write publishes at once, the assistant only a proposal its user confirmed on the
// card. The publication itself is the shared PublishPageHandler: channel and token from IRequestChannel, the owner as publisher, the
// live-edit guard of D3 under the page lock, the CAS on the expected version and the change summary rules of the web.

public sealed class PreviewMcpPublishHandler(ISession session, PageReader reader, IContentAccess access, McpVisibility visibility,
    CollaborationCheckpoints checkpoints, McpPublishSummaries summaries)
{
    private const int MaximumChangeSummaryLength = 500;

    public async Task<McpPublishSummary> Handle(PreviewMcpPublish query, CancellationToken ct)
    {
        Rules.OptionalLine(query.ChangeSummary, "Popis změny", MaximumChangeSummaryLength);
        var (read, actor, page) = await ReadAsync(query.PageId, query.ExpectedVersion, ct);
        if (read.PublishedVersion == read.Version)
        {
            throw new ApiException(409, $"Verze {read.Version} stránky už je publikovaná; není co publikovat.", "already_published");
        }
        await checkpoints.RequireNoForeignUnsavedAsync(page.Id, actor.Id, ct, includeOwn: true);
        return await summaries.BuildAsync(actor, page, ct);
    }

    private async Task<(PageDto Read, UserAccount Actor, Page Page)> ReadAsync(Guid pageId, int expectedVersion, CancellationToken ct)
    {
        // The working revision only with EditContent (anyone else: the 404 of a missing page), no opening recorded.
        var read = await reader.ReadAsync(pageId, PageReadVersion.Working, false, ct);
        await McpPublishChannel.RequireInScopeAsync(visibility, read.SpaceId, ct);
        if (read.Version != expectedVersion)
        {
            throw McpWriteErrors.VersionConflict(read.Version);
        }
        var actor = await access.LockActorAsync(read.SpaceId, ct);
        return (read, actor, (await session.GetAsync<Page>(pageId, ct))!);
    }

    internal static async Task<bool> PublishedRevisionIsCurrentAsync(ISession session, Guid pageId, CancellationToken ct)
    {
        var page = await session.GetAsync<Page>(pageId, ct);
        if (page?.PublishedRevisionId is not { } published)
        {
            return false;
        }
        var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null).Select(x => x.Id).SingleOrDefaultAsync<Guid>(ct);
        return current == published;
    }
}

/// <summary>
/// WP-41, spec §6.3: the assistant publishes a proposal its owner confirmed on the card: the approved-proposal marker on the channel assistant (else 403), G5, then the shared <see cref="PublishPageHandler"/>
/// (EditContent, the expected version from the time of the proposal, anyone's unsaved live edits refused for the channel assistant,
/// the channel on the publication).
/// </summary>
public sealed class PublishMcpApprovedHandler(ISession session, McpVisibility visibility, PublishPageHandler publish, IRequestChannel channel,
    IHttpContextAccessor http)
{
    public async Task<McpPublishOutcome> Handle(PublishMcpApproved command, CancellationToken ct)
    {
        McpPublishChannel.RequireApprovedProposal(channel, http);
        await visibility.RequirePageAsync(command.PageId, ct);
        var publishedBefore = await PreviewMcpPublishHandler.PublishedRevisionIsCurrentAsync(session, command.PageId, ct);
        PageDto page;
        try
        {
            page = await publish.Handle(new PublishPage(command.PageId, new PublishPageRequest(command.ExpectedVersion, command.ChangeSummary)), ct);
        }
        catch (ApiException error) when (error.Code == "version_conflict")
        {
            throw McpWriteErrors.VersionConflict((await session.GetAsync<Page>(command.PageId, ct))!.Version);
        }
        return new McpPublishOutcome(page.Id, page.SpaceId, page.Version, page.PublishedVersion, !publishedBefore);
    }
}

/// <summary>
/// A token with the scope write publishes at once (3.6: the former direct is write): the scope is checked again here
/// (the tool only chose the path), then G5 and the shared <see cref="PublishPageHandler"/> as the web: EditContent,
/// the expected version, anyone's unsaved live edits refused for the channel mcp, the channel and the token on the publication.
/// </summary>
public sealed class PublishMcpDirectHandler(ISession session, McpVisibility visibility, PublishPageHandler publish, IRequestChannel channel,
    IHttpContextAccessor http, IAuthorizationService authorization)
{
    public async Task<McpPublishOutcome> Handle(PublishMcpDirect command, CancellationToken ct)
    {
        McpPublishChannel.RequireToken(channel);
        if (!await McpToolScopes.AllowsAsync(authorization, http.HttpContext?.User, ApiScopes.Write))
        {
            throw McpPublishRules.Refusal("Token nemá rozsah Zápis.");
        }
        await visibility.RequirePageAsync(command.PageId, ct);
        var publishedBefore = await PreviewMcpPublishHandler.PublishedRevisionIsCurrentAsync(session, command.PageId, ct);
        PageDto page;
        try
        {
            page = await publish.Handle(new PublishPage(command.PageId, new PublishPageRequest(command.ExpectedVersion, command.ChangeSummary)), ct);
        }
        catch (ApiException error) when (error.Code == "version_conflict")
        {
            throw McpWriteErrors.VersionConflict((await session.GetAsync<Page>(command.PageId, ct))!.Version);
        }
        return new McpPublishOutcome(page.Id, page.SpaceId, page.Version, page.PublishedVersion, !publishedBefore);
    }
}
