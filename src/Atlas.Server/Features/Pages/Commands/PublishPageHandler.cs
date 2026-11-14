using NHibernate;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Commands;

public sealed class PublishPageHandler
{
    private const int MaximumChangeSummaryLength = 500;
    public const string UnsavedLiveEditsMessage = "Stránka má neuložené živé úpravy, které by publikace vynechala. Počkejte, až se uloží (editor ukáže „Uloženo“), a zkuste to znovu, nebo stránku publikujte v Atlasu.";

    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly PageReader m_reader;
    private readonly CollaborationCheckpoints m_checkpoints;
    private readonly IRequestChannel m_channel;
    private readonly Outbox m_outbox;

    public PublishPageHandler(ISession session, IContentAccess access, IAtlasLocks locks, PageReader reader, CollaborationCheckpoints checkpoints,
        IRequestChannel channel, Outbox outbox)
    {
        m_outbox = outbox;
        m_checkpoints = checkpoints;
        m_channel = channel;
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_reader = reader;
    }

    public async Task<PageDto> Handle(PublishPage command, CancellationToken ct)
    {
        // Before any lock: a bad summary is refused without touching the page. It is ignored (not even stored) when the early return below applies.
        var changeSummary = Rules.OptionalLine(command.Request.ChangeSummary, "Popis změny", MaximumChangeSummaryLength);
        // Legacy lock order: the shared advisory comes first, before anything reads content.
        // LockActorAsync takes it again below, which is a no-op once it is held.
        await m_locks.AcquireContentSharedAsync(ct);
        // The space is read before the space lock and re-checked against the locked row below: a concurrent move answers 409 space_changed.
        var spaceId = await m_session.QueryOver<Page>()
            .Where(x => x.Id == command.Id)
            .Select(x => x.SpaceId)
            .SingleOrDefaultAsync<Guid>(ct);
        if (spaceId == Guid.Empty)
        {
            throw new ApiException(404, "Stránka nebyla nalezena.");
        }
        var actor = await m_access.LockActorAsync(spaceId, ct);
        if (command.Review is not null && !await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "Koncepty může schvalovat pouze správce wiki.");
        }
        var page = await m_session.GetAsync<Page>(command.Id, LockMode.Upgrade, ct) ?? throw new ApiException(404, "Stránka nebyla nalezena.");
        // An earlier MCP check may already have loaded this entity. Upgrading its lock does not refresh NHibernate's identity map.
        await m_session.RefreshAsync(page, ct);
        if (page.SpaceId != spaceId)
        {
            // A cross-space move committed between the projection above and this lock. PageGateMovedPageTests interleaves it for
            // the same check in SavePageHandler; Publish has no row there yet.
            throw new ApiException(409, "Stránka byla přesunuta. Načtěte ji znovu.", "space_changed");
        }
        if (page.IsTrashed)
        {
            throw new ApiException(404, "Stránka nebyla nalezena.");
        }
        await m_access.RequirePageAsync(page, AccessOperation.EditContent, ct);
        var current = await m_session.QueryOver<PageRevision>()
            .Where(x => x.PageId == command.Id && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        ValidateReview(command, page, current);
        Rules.ExpectVersion(page.Version, command.Request.ExpectedVersion);
        if (page.PublishedRevisionId == current.Id)
        {
            return await m_reader.ReadEditedWithoutProofAsync(command.Id, ct);
        }
        var channel = m_channel.Current;
        if (channel.RefusesOwnUnsaved)
        {
            // D3: publishing never resets and stays unguarded for web and api. An agent (mcp, assistant) must not publish the saved revision while
            // anyone, the token owner included, has unsaved live edits that the publication would silently leave out.
            await m_checkpoints.RequireNoForeignUnsavedAsync(page.Id, actor.Id, ct, includeOwn: true);
        }
        else if (command.Request.RefuseUnsavedLiveEdits)
        {
            // Publikovat změny outside editing: edits typed in a window that closed live only in the shared log, and publishing the
            // saved revision silently left them out (UX EP2-8).
            await m_checkpoints.RequireNoForeignUnsavedAsync(page.Id, actor.Id, ct, UnsavedLiveEditsMessage, includeOwn: true);
        }
        await PagePublications.RecordAsync(m_session, m_outbox, page, current.Id, actor.Id, changeSummary, channel.Code, channel.ApiTokenId, ct);
        return await m_reader.ReadEditedWithoutProofAsync(command.Id, ct);
    }

    private static void ValidateReview(PublishPage command, Page page, PageRevision current)
    {
        if (command.Review is not { } review)
        {
            return;
        }
        if (page.SpaceId != review.SpaceId)
        {
            throw new ApiException(409, "Stránka již není v tomto prostoru. Obnovte seznam konceptů.", "space_changed");
        }
        if (current.Id != review.RevisionId || page.Version != command.Request.ExpectedVersion)
        {
            throw new ApiException(409, "Koncept se od načtení změnil. Před schválením zkontrolujte novou verzi.", "draft_changed");
        }
    }
}
