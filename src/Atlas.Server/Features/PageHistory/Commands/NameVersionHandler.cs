using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.PageHistory.Domain;
using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageHistory.Commands;

public sealed class NameVersionHandler
{
    private readonly ISession m_session;
    private readonly PageHistoryAccess m_pages;
    private readonly Outbox m_outbox;
    private readonly McpOrganizationGuard m_mcp;
    private readonly IRequestChannel m_channel;

    public NameVersionHandler(ISession session, PageHistoryAccess pages, Outbox outbox, McpOrganizationGuard mcp, IRequestChannel channel)
    {
        m_mcp = mcp;
        m_channel = channel;
        m_session = session;
        m_pages = pages;
        m_outbox = outbox;
    }

    public async Task<NamedVersion> Handle(NameVersion command, CancellationToken ct)
    {
        var label = string.IsNullOrWhiteSpace(command.Label) ? null : command.Label.Trim();
        if (label is { Length: > 500 } || label?.Any(char.IsControl) == true)
        {
            throw new ApiException(400, "Popis verze smí mít nejvýše 500 znaků na jednom řádku.");
        }
        // G5 on MCP and the assistant (atlas_name_version) under the space lock: a space out of AI is a missing page.
        if (m_channel.Current.IsAiBound)
        {
            await m_mcp.LockPageSpaceSharedAsync(command.Id, ct);
        }
        var (page, _) = await m_pages.RequirePageAsync(command.Id, true, ct);
        Rules.ExpectVersion(page.Version, command.ExpectedVersion);
        // Metadata of the immutable revision only: no trigger watches these columns, and the content stays as it was saved. The page's
        // lock (FOR UPDATE) keeps the read and the update one step.
        var current = await m_session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null)
            .Select(x => x.Id, x => x.IsExplicit).SingleOrDefaultAsync<object[]>(ct);
        var (revisionId, closed) = ((Guid)current[0], (bool)current[1]);
        await m_session.CreateSQLQuery("""UPDATE "PageRevision" SET "IsExplicit"=true,"Label"=:label WHERE "Id"=:id""")
            .SetParameter("id", revisionId).SetParameter("label", label, NHibernate.NHibernateUtil.String)
            .ExecuteUpdateAsync(ct);
        if (!closed && m_outbox.IsConsumed<VersionClosed>())
        {
            m_outbox.Enqueue(new VersionClosed(page.Id, revisionId, VersionCloseReasons.Named), page.Id.ToString());
        }
        return new NamedVersion(revisionId, page.Version);
    }
}
