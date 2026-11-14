using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Vaults.References;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Queries;

/// <summary>3.6-I Živě: the live document a reader of a live page follows read-only. Unlike OpenCollaboration it never starts, resets or
/// upgrades a generation. A page that is not live for this reader answers 404 like a missing page; without a live state it answers 404
/// <c>no_live_session</c>, and the reader stays on the saved page until PageChanged.</summary>
public sealed record WatchCollaboration(Guid PageId);

public sealed class WatchCollaborationHandler(ISession session, PageGate gate, IAtlasLocks locks, CollaborationSecretReferences references)
{
    public async Task<WikiCollaborationSession> Handle(WatchCollaboration query, CancellationToken ct)
    {
        return await WatchAsync((await gate.ReadAsync(query.PageId, AccessOperation.ReadContent, ct)).Require(), ct);
    }

    /// <summary>The watch of a page already opened for ReadContent in this transaction (the page bundle).</summary>
    public async Task<WikiCollaborationSession> WatchAsync(LockedPage opened, CancellationToken ct)
    {
        if (!opened.CanEdit && (opened.Page.PublishedRevisionId is null
                || (opened.Page.PublicationMode ?? locks.SharedSpacePublicationMode(opened.Page.SpaceId)
                    ?? await PublicationModes.EffectiveAsync(session, opened.Page, ct)) != PublicationMode.Live))
        {
            throw new ApiException(404, "Stránka nebyla nalezena.");
        }
        var state = await session.GetAsync<PageCollaboration>(opened.Page.Id, ct)
            ?? throw new ApiException(404, "Stránku teď nikdo neupravuje.", "no_live_session");
        if (!await references.CanJoinAsync(opened.Actor, opened.Page.Id, state.Generation, state.BaseRevisionId, ct))
        {
            throw new ApiException(403, SecretReferenceAccess.HiddenMessage, "hidden_secret_references");
        }
        return new WikiCollaborationSession(new WikiDocumentId(opened.Page.Id, state.Generation).ToString(), opened.Page.Version, state.Generation);
    }
}
