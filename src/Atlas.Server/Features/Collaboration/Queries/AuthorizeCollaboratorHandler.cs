using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Queries;

public sealed class AuthorizeCollaboratorHandler
{
    private readonly ISession m_session;
    private readonly ResourceGrantSnapshots m_grants;
    private readonly CollaborationSecretReferences m_references;

    public AuthorizeCollaboratorHandler(ISession session, ResourceGrantSnapshots grants, CollaborationSecretReferences references)
    {
        m_session = session;
        m_grants = grants;
        m_references = references;
    }

    public async Task<CollaboratorAuthorization> Handle(AuthorizeCollaborator query, CancellationToken ct)
    {
        var actor = await m_session.GetAsync<UserAccount>(query.UserId, ct);
        if (actor is null || !actor.IsActive
            || query.SessionId is { } sessionId && await BrowserSessionRevocations.IsRevokedAsync(m_session, sessionId, ct))
        {
            return CollaboratorAuthorization.Denied;
        }
        var page = await m_session.GetAsync<Page>(query.PageId, ct);
        if (page is null || page.IsTrashed)
        {
            return CollaboratorAuthorization.Denied;
        }
        // 3.6-I Živě: a reader of a live page follows it read-only; ReadContent keeps the private new page rule (PageVisibility).
        var canWrite = (await m_grants.AuthorizePageAsync(actor, page, AccessOperation.EditContent, ct)).Allowed;
        if (!canWrite && !(page.PublishedRevisionId is not null
                && await PublicationModes.EffectiveAsync(m_session, page, ct) == PublicationMode.Live
                && (await m_grants.AuthorizePageAsync(actor, page, AccessOperation.ReadContent, ct)).Allowed))
        {
            return CollaboratorAuthorization.Denied;
        }
        var state = await m_session.QueryOver<PageCollaboration>()
            .Where(x => x.PageId == query.PageId)
            .Select(x => x.Generation, x => x.BaseRevisionId)
            .SingleOrDefaultAsync<object[]>(ct);
        if (state is null || (Guid)state[0] != query.Generation)
        {
            return CollaboratorAuthorization.Denied;
        }
        // WP4 V6: the live state is shared unfiltered, so an account (writer or reader) who may not Discover one of its password
        // references reads the page instead (the join, the periodic refresh and AccessChanges after a revocation all end here).
        return await m_references.CanJoinAsync(actor, query.PageId, query.Generation, (Guid)state[1], ct)
            ? new CollaboratorAuthorization(canWrite, actor.SecurityStamp, true)
            : CollaboratorAuthorization.Denied;
    }
}
