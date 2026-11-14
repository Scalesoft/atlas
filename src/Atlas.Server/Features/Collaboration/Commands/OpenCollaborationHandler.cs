using Atlas.Server.Features.Vaults.References;
using NHibernate;
using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Commands;

public sealed class OpenCollaborationHandler
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_currentActor;
    private readonly CollaborationAccess m_access;
    private readonly CollaborationSecretReferences m_references;
    private readonly RequirementPageConversion m_conversion;

    public OpenCollaborationHandler(ISession session, ICurrentActor currentActor, CollaborationAccess access, CollaborationSecretReferences references,
        RequirementPageConversion conversion)
    {
        m_conversion = conversion;
        m_session = session;
        m_currentActor = currentActor;
        m_access = access;
        m_references = references;
    }

    public async Task<WikiCollaborationSession> Handle(OpenCollaboration command, CancellationToken ct)
    {
        var requested = await m_currentActor.RequireAsync(ct);
        var page = await m_access.RequireWriterAsync(command.PageId, requested.Id, requested.SecurityStamp, ct);
        var current = await m_session.QueryOver<PageRevision>()
            .Where(x => x.PageId == command.PageId && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct)
            ?? throw CollaborationErrors.Stale();
        // 3.6 repair on open, before the live state is read: a converted page's checkpoints are reset, so the session seeds from it.
        if (await m_conversion.RepairOnOpenAsync(command.PageId, current.Id, current.ContentJson, ct))
        {
            await m_session.RefreshAsync(current, ct);
        }
        var state = await m_session.GetAsync<PageCollaboration>(command.PageId, LockMode.Upgrade, ct);
        if (state is null)
        {
            state = new PageCollaboration { PageId = command.PageId, Generation = Guid.NewGuid(), BaseRevisionId = current.Id };
            await m_session.SaveAsync(state, ct);
        }
        if (state.BaseRevisionId != current.Id)
        {
            // A live write's revision whose update never reached the log (the process ended between them, LivePageWrites). People's
            // edits were saved before it and the relay takes none since (AppendUpdateHandler), so the revision holds everything: start from it.
            await new CollaborationCheckpoints(m_session).ResetAsync(page, current, ct);
        }
        var upgraded = false;
        if (CollaborationSchema.StartsFromRevision(state, current))
        {
            // Nothing unsaved lives only in the relay: the next session seeds from the saved revision, so an editor never
            // opens (and later saves) a live state that lost saved content, e.g. an OpenAPI block after an editor upgrade.
            await new CollaborationCheckpoints(m_session).ResetAsync(page, current, ct);
        }
        else
        {
            upgraded = CollaborationSchema.UpgradeKnownSchema(state);
        }
        if (!await m_references.CanJoinAsync(requested, command.PageId, state.Generation, state.BaseRevisionId, ct))
        {
            throw new ApiException(403, SecretReferenceAccess.HiddenMessage, "hidden_secret_references");
        }
        return new WikiCollaborationSession(new WikiDocumentId(command.PageId, state.Generation).ToString(), page.Version, state.Generation, upgraded);
    }
}
