using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.PageDecorations;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Domain;

/// <summary>The single page read: the query handler and every command that answers with a PageDto share it.</summary>
public sealed class PageReader
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly PageGate m_gate;
    private readonly PageDecorationReader m_decorations;
    private readonly SecretReferenceAccess m_references;
    private readonly IRequestChannel m_channel;
    private readonly IPageCatalogMark? m_catalog;
    private readonly RequirementPageConversion m_conversion;

    public PageReader(ISession session, IContentAccess access, IAtlasLocks locks, PageGate gate, PageDecorationReader decorations,
        SecretReferenceAccess references, IRequestChannel channel, IEnumerable<IPageCatalogMark> catalog, RequirementPageConversion conversion)
    {
        m_conversion = conversion;
        m_catalog = catalog.FirstOrDefault();
        m_channel = channel;
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_gate = gate;
        m_decorations = decorations;
        m_references = references;
    }

    public Task<PageDto> ReadAsync(Guid id, CancellationToken ct, bool recordOpening = false)
    {
        return ReadAsync(id, PageReadVersion.Default, recordOpening, ct);
    }

    /// <summary>A read of one chosen version (MCP): <see cref="PageReadVersion.Published"/> also for an editor, and
    /// <see cref="PageReadVersion.Working"/> only with EditContent; anyone else gets the 404 of a missing page, which never tells that a
    /// working revision exists. An editor asking for the publication of a never published page gets 404 not_published.</summary>
    public async Task<PageDto> ReadAsync(Guid id, PageReadVersion version, bool recordOpening, CancellationToken ct)
    {
        // Commands call this after mutating the page, and the gate re-reads the row of a page the session holds: flush first so it
        // keeps the version bump.
        await m_session.FlushAsync(ct);
        // ReadContent includes the private draft rule; readers see the publication, editors the working revision.
        var opened = (await m_gate.ReadAsync(id, AccessOperation.ReadContent, ct)).Require();
        return await BuildAsync(opened.Page, opened.Actor, opened.CanEdit, version, recordOpening, ct);
    }

    /// <summary>The page view's read (<see cref="ReadAsync(Guid, CancellationToken, bool)"/> recording the opening) with the page it
    /// opened, for the reads that follow it in the same transaction under its locks (PageBundle).</summary>
    public async Task<(PageDto Page, LockedPage Opened)> ReadOpenedAsync(Guid id, CancellationToken ct)
    {
        await m_session.FlushAsync(ct);
        var opened = (await m_gate.ReadAsync(id, AccessOperation.ReadContent, ct)).Require();
        return (await BuildAsync(opened.Page, opened.Actor, opened.CanEdit, PageReadVersion.Default, true, ct), opened);
    }

    /// <summary>
    /// The read-back of a command on the proof of the page it opened for editing: the EditablePage is the decision of
    /// EditContent (or more), made earlier in the same transaction under locks it still holds, so the page is not decided a
    /// second time and is read as an editor reads it. The reader does the reading: it locks and re-reads the account and the
    /// page row, as the read of an answer always did. A row it then finds outside the space of the proof was moved by the
    /// command after its gate: InvalidOperationException, not the 404 of a missing page.
    /// </summary>
    public async Task<PageDto> ReadEditedAsync(EditablePage edited, CancellationToken ct)
    {
        // A capability of an ended transaction proves nothing, and neither does one of another scope's gate, whose locks are
        // not this session's: the reader asks its own gate, and either fails before anything is sent.
        m_gate.RequireOwn(edited);
        // The lock below re-reads the row the command changed: flush first so it keeps the change.
        await m_session.FlushAsync(ct);
        // The space is the one the gate locked for the page, not the one the instance names: the instance is the command's
        // to change, and a space it names was locked by nobody.
        return await ReadBackAsync(edited.Page.Id, edited.Space.SpaceId, true, ct);
    }

    /// <summary>
    /// <see cref="ReadEditedAsync"/> for a handler that still opens its page itself and so has no EditablePage to show: the
    /// caller's word stands in for the proof that it required EditContent (or more) on this page earlier in the same
    /// transaction. Not for new code. Its callers are PublishPageHandler and SetPageTemplateHandler, and
    /// PageGateCapabilityConventionTests allows no other; it goes when the second of them opens its page through PageGate.
    /// </summary>
    public async Task<PageDto> ReadEditedWithoutProofAsync(Guid id, CancellationToken ct)
    {
        // The caller mutated the page, and the locked read below re-reads the row: flush first so it keeps the version bump.
        await m_session.FlushAsync(ct);
        // Lock order: the shared advisory comes first, before anything reads content.
        // LockActorAsync takes it again below, which is a no-op once it is held.
        await m_locks.AcquireContentSharedAsync(ct);
        // The space is locked before the page: the command already holds the page.
        var originalSpace = m_session.Held<Page>(id)?.SpaceId ?? await SpaceOfAsync(id, ct);
        if (originalSpace == Guid.Empty)
        {
            throw MissingPage();
        }
        return await ReadBackAsync(id, originalSpace, false, ct);
    }

    /// <summary>The statements of a read-back: the account locked and re-read with the actor variable, the page row locked
    /// FOR SHARE and re-read. No decision; the page is read as its editor reads it.</summary>
    /// <param name="spaceOfProof">Whether <paramref name="spaceId"/> is the space a gate locked for the page (an
    /// EditablePage), not one the reader looked up itself.</param>
    private async Task<PageDto> ReadBackAsync(Guid id, Guid spaceId, bool spaceOfProof, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(spaceId, ct);
        var page = await m_locks.ReadPageSharedAsync(id, ct);
        if (page is null || page.IsTrashed)
        {
            throw MissingPage();
        }
        if (page.SpaceId != spaceId)
        {
            // The gate locked the row FOR UPDATE in the space of the proof, so no other transaction moved it: the command did,
            // after its gate. That is a programming error and fails as one. A 404 would tell the client that a page it may
            // edit is gone. Without the proof the space is the reader's own look, and the page is missing as before.
            throw spaceOfProof
                ? new InvalidOperationException(
                    "The page left the space its gate locked for it; a command does not move a page it opened for editing and then read it back on that proof.")
                : MissingPage();
        }
        return await BuildAsync(page, actor, true, PageReadVersion.Default, false, ct);
    }

    private async Task<PageDto> BuildAsync(Page page, UserAccount actor, bool canEdit, PageReadVersion version, bool recordOpening, CancellationToken ct)
    {
        var id = page.Id;
        // 3.6-I Živě: a reader of a live page reads the latest saved state, as its editors do; the mode is read only for a reader.
        var live = (version == PageReadVersion.PublishedOrLive || !canEdit && version != PageReadVersion.Published) && page.PublishedRevisionId is not null
            && (page.PublicationMode ?? m_locks.SharedSpacePublicationMode(page.SpaceId) ?? await PublicationModes.EffectiveAsync(m_session, page, ct))
                == PublicationMode.Live;
        if (version == PageReadVersion.Working && !canEdit && !live)
        {
            throw MissingPage();
        }
        var working = version == PageReadVersion.Working || version == PageReadVersion.Default && (canEdit || live) || version == PageReadVersion.PublishedOrLive && live;
        var current = working
            ? await m_session.QueryOver<PageRevision>().Where(x => x.PageId == id && x.ActiveTo == null).SingleOrDefaultAsync(ct)
            : page.PublishedRevisionId is { } publishedId ? await m_session.GetAsync<PageRevision>(publishedId, ct) : null;
        // 3.6 repair on open: a page still holding requirement blocks is converted before anyone reads it (nothing when it holds none).
        if (current is not null && await m_conversion.RepairOnOpenAsync(id, current.Id, current.ContentJson, ct))
        {
            await m_session.RefreshAsync(current, ct);
        }
        if (current is null)
        {
            throw version is PageReadVersion.Published or PageReadVersion.PublishedOrLive && canEdit
                ? new ApiException(404, "Stránka zatím nemá publikovanou verzi.", "not_published")
                : MissingPage();
        }
        int? publishedVersion = null;
        if (page.PublishedRevisionId is { } revisionId)
        {
            publishedVersion = current.Id == revisionId
                ? current.Version
                : await m_session.QueryOver<PageRevision>().Where(x => x.Id == revisionId).Select(x => x.Version).SingleOrDefaultAsync<int>(ct);
        }
        // WP4 V6: password references the reader may not Discover are removed on the server; such a copy is read-only, since
        // saving it would drop what the reader cannot see (SecretReferenceAccess.RequireEditableAsync refuses it too).
        var (contentJson, hidden) = await m_references.ForReaderAsync(actor, current.ContentJson, ct);
        var result = new PageDto(page.Id, page.SpaceId, page.ParentId, current.Title, page.Version, publishedVersion is not null,
            contentJson, await m_references.MarkdownForReaderAsync(actor, current.Markdown, ct), publishedVersion, current.ActiveFrom, current.ActiveTo, canEdit && !hidden)
        {
            Decoration = await m_decorations.ReadAsync(page.Id, ct),
            IsTemplate = page.IsTemplate,
            OwnAccess = page.OwnAccess,
            IsCatalog = m_catalog is not null && await m_catalog.IsCatalogAsync(page.Id, ct),
            Live = live
        };
        if (recordOpening)
            await Features.PageAccess.PageAccessAudit.RecordOpeningAsync(m_session, page.Id, current.Id, actor.Id, null, m_channel.Current, ct);
        return result;
    }

    private Task<Guid> SpaceOfAsync(Guid id, CancellationToken ct)
    {
        return m_session.QueryOver<Page>().Where(x => x.Id == id).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
    }

    private static ApiException MissingPage()
    {
        return new ApiException(404, "Stránka nebyla nalezena.");
    }
}

/// <summary>Whether a page is a catalog page (<see cref="PageDto.IsCatalog"/>): registered by the Projector module only while it is on.</summary>
public interface IPageCatalogMark
{
    Task<bool> IsCatalogAsync(Guid pageId, CancellationToken ct);
}

/// <summary>Which revision a page read returns: Default is the working revision for an editor and the publication for anyone else.</summary>
public enum PageReadVersion
{
    Default,
    Published,
    /// <summary>The publication, except that a page in Živě mode reads its live state, as REST and the UI serve it (MCP).</summary>
    PublishedOrLive,
    Working
}
