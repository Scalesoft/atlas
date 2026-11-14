using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// The way to a page: takes the content locks in their one order, reads the page as it is under its row lock and decides it for
/// the request's account. The order is the shared content lock, the space row, the account with the actor variable, the page
/// row. A read takes the space and the page FOR SHARE, an edit the page FOR UPDATE, a restructure also the space FOR UPDATE,
/// before its FOR SHARE. Each sends the statements PageGateLockTraceTests spells out, which are those of the handlers that
/// opened a page themselves, so a handler moves to the gate without a change of its lock trace. The history read alone
/// changed when it moved: it locked the page row and loaded it in a second statement, and sends one now.
/// </summary>
/// <remarks>
/// <para>One more difference waits for a caller. ReadAsync takes the space of a page the session already holds from that
/// instance, as PageReader always did and needs for the read-back of a restore or a create, where the other reads it replaced
/// (page connections, editor assist, the writing read, history) looked the space up in a statement. None of those is reached
/// with a held page today: each arrives with an empty session.</para>
/// <para>Built on IContentAccess and IAtlasLocks, which stay the lower layer for the paths outside: another account than the
/// request's (collaboration relay, share links, export), the exclusive administration ladder, imports, two spaces at once.</para>
/// <para>Kept by construction: the page a gate returns is the row under its lock also when the session already held the
/// instance (50d9693f, 3d596677), FOR UPDATE through ISession.GetForUpdateAsync and FOR SHARE through
/// IAtlasLocks.ReadPageSharedAsync; and within an operation a space is taken FOR UPDATE before its FOR SHARE, never after
/// (c54d1c0f).</para>
/// <para>Left to the caller, because the gate sees one call at a time. What inserts a page or changes the tree enters the space
/// for change before anything else in the transaction locks that space; the space it gets then says so
/// (LockedSpace.ForChange), and what inserts a page requires that of the space it is given (RequireOwnForChange), so the
/// space of a page that was only read or edited does not pass. And a page is never changed before its gate:
/// re-reading a held instance drops changes that are not flushed yet, so a command that reads a page back after changing it
/// flushes first, as PageReader does.</para>
/// <para>Scoped like the session: one per message, and the locks it took end with the message's transaction. So does what it
/// handed out: a LockedSpace, LockedPage or EditablePage remembers that transaction and refuses to be used after it
/// (RequireHeld). It is the proof of this gate alone: what takes a page as its proof asks the gate of its own scope
/// (RequireOwn), so a page that another scope opened does not pass while that scope's transaction runs.</para>
/// </remarks>
public sealed class PageGate
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;

    public PageGate(ISession session, IContentAccess access, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    /// <summary>The page FOR SHARE, decided for <paramref name="operation"/>: a read may ask for more than ReadContent (history
    /// and editor assist read with EditContent). A page the session already holds names its space without a statement, which is
    /// how a command reads a page back.</summary>
    public async Task<PageOpening<LockedPage>> ReadAsync(Guid pageId, AccessOperation operation, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        var spaceId = m_session.Held<Page>(pageId)?.SpaceId ?? await SpaceOfAsync(pageId, ct);
        if (spaceId == Guid.Empty)
        {
            return NoPage<LockedPage>(operation, false);
        }
        return await ReadPageAsync(await EnterSpaceAsync(spaceId, ct), pageId, operation, ct);
    }

    /// <summary>The page FOR UPDATE under the space FOR SHARE, decided for EditContent or ManageAccess: a change of the page's
    /// own content or state that leaves the tree and the access of the space alone (save, publish, restore, source).</summary>
    public async Task<PageOpening<EditablePage>> EditAsync(Guid pageId, AccessOperation operation, CancellationToken ct)
    {
        RequireEditOperation(operation);
        await m_locks.AcquireContentSharedAsync(ct);
        var spaceId = await SpaceOfAsync(pageId, ct);
        if (spaceId == Guid.Empty)
        {
            return NoPage<EditablePage>(operation, true);
        }
        return await EditPageAsync(await EnterSpaceAsync(spaceId, ct), pageId, operation, ct);
    }

    /// <summary>The page FOR UPDATE under the space FOR UPDATE, decided for EditContent or ManageAccess: a change that the space
    /// row serializes (the tree and its order, the trash, item grants and the access mode, and what shares their lock today:
    /// rename, tags, decoration).</summary>
    public async Task<PageOpening<EditablePage>> RestructureAsync(Guid pageId, AccessOperation operation, CancellationToken ct)
    {
        RequireEditOperation(operation);
        await m_locks.AcquireContentSharedAsync(ct);
        var spaceId = await SpaceOfAsync(pageId, ct);
        if (spaceId == Guid.Empty || !await m_locks.LockSpaceForUpdateAsync(spaceId, ct))
        {
            return NoPage<EditablePage>(operation, true);
        }
        // The space is already held FOR UPDATE: the FOR SHARE of LockActorAsync is compatible and lets the page decision see it locked.
        return await EditPageAsync(await EnterAsync(spaceId, true, ct), pageId, operation, ct);
    }

    /// <summary>The shared content lock, the space FOR SHARE and the account. A missing space is the 404 of
    /// IAtlasLocks.LockSpaceSharedAsync.</summary>
    public Task<LockedSpace> EnterSpaceAsync(Guid spaceId, CancellationToken ct)
    {
        return EnterAsync(spaceId, false, ct);
    }

    /// <summary>
    /// The shared content lock, the space FOR UPDATE, then its FOR SHARE and the account: for what inserts a page or changes
    /// the space. A page's insert takes the space FOR UPDATE (ContentRowEffects); an insert under a space held only FOR SHARE has
    /// to upgrade it, and two of them wait for each other (40P01, c54d1c0f). A missing space is the 404 of
    /// IAtlasLocks.LockSpaceSharedAsync, as from <see cref="EnterSpaceAsync"/>. The space it returns says that it was entered
    /// for change (<see cref="LockedSpace.ForChange"/>), which what inserts a page requires of the space it is given.
    /// </summary>
    public async Task<LockedSpace> EnterSpaceForChangeAsync(Guid spaceId, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        // A missing space is left to LockActorAsync and its 404. A row that was committed between the two statements is held
        // FOR SHARE alone, and the space says so.
        var forChange = await m_locks.LockSpaceForUpdateAsync(spaceId, ct);
        return await EnterAsync(spaceId, forChange, ct);
    }

    /// <param name="forChange">Whether this transaction already holds the space row FOR UPDATE.</param>
    private async Task<LockedSpace> EnterAsync(Guid spaceId, bool forChange, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(spaceId, ct);
        return new LockedSpace(this, spaceId, actor, m_session.GetCurrentTransaction(), forChange);
    }

    /// <summary>
    /// The locks a capability stands for end with the transaction that took them. A LockedSpace, LockedPage or EditablePage
    /// kept beyond it and used in a later transaction is a programming error and fails as one, not as an ApiException.
    /// </summary>
    internal void RequireCurrent(ITransaction taken)
    {
        if (!taken.IsActive || !ReferenceEquals(m_session.GetCurrentTransaction(), taken))
        {
            throw new InvalidOperationException(
                "The locks behind this page or space ended with their transaction; open the page or enter the space again in the current one.");
        }
    }

    /// <summary>
    /// <see cref="RequireCurrent"/> for what takes a page as its proof and then works on the session of this gate: the page
    /// must also be one this gate opened. The gate of another scope has a session of its own, and while that session's
    /// transaction runs it calls the page held, which says nothing about the locks of this one. A programming error like the
    /// other, and it fails as one.
    /// </summary>
    internal void RequireOwn(EditablePage edited)
    {
        RequireOwn(edited.Space);
    }

    /// <summary><see cref="RequireOwn(EditablePage)"/> for a space: this gate entered it, in the transaction that still runs.</summary>
    internal void RequireOwn(LockedSpace space)
    {
        RequireHandedOut(space);
        space.RequireHeld();
    }

    /// <summary>
    /// <see cref="RequireOwn(LockedSpace)"/>, and the space was entered for change (<see cref="LockedSpace.ForChange"/>). What
    /// inserts a page takes the space as its proof and calls this first, on the gate of its own scope: the insert goes out on
    /// that scope's session, and a space another scope entered for change is held FOR UPDATE over there, not here. Public,
    /// because nothing in the server inserts a page through the gate yet and its test has to reach it.
    /// </summary>
    public void RequireOwnForChange(LockedSpace space)
    {
        RequireHandedOut(space);
        space.RequireHeldForChange();
    }

    private void RequireHandedOut(LockedSpace space)
    {
        if (!space.IsOf(this))
        {
            throw new InvalidOperationException(
                "This page or space was opened by the gate of another scope, and its locks are not this session's; open it through the gate of the scope that uses it.");
        }
    }

    internal async Task<PageOpening<LockedPage>> ReadPageAsync(LockedSpace space, Guid pageId, AccessOperation operation, CancellationToken ct)
    {
        // LockedSpace.ReadPageAsync passes the gate that entered the space. A caller inside the server could pass another
        // gate's space: this gate would lock the page row on its own session and hand out a page under locks it never took.
        RequireOwn(space);
        var page = await m_locks.ReadPageSharedAsync(pageId, ct);
        var (state, decision) = await DecideAsync(page, space.SpaceId, operation, ct);
        return new PageOpening<LockedPage>(state, space.Actor, decision,
            state == PageOpeningState.Open ? new LockedPage(page!, space, operation, decision.CanEdit) : null, false);
    }

    private async Task<PageOpening<EditablePage>> EditPageAsync(LockedSpace space, Guid pageId, AccessOperation operation, CancellationToken ct)
    {
        var page = await m_session.GetForUpdateAsync<Page>(pageId, ct);
        var (state, decision) = await DecideAsync(page, space.SpaceId, operation, ct);
        return new PageOpening<EditablePage>(state, space.Actor, decision,
            state == PageOpeningState.Open ? new EditablePage(page!, space, operation) : null, true);
    }

    /// <summary>The checks in the order of PageOpeningState. A page that is missing, moved or in the trash is not decided for
    /// the account, so it costs no grant query.</summary>
    private async Task<(PageOpeningState State, PageAuthorization Decision)> DecideAsync(Page? page, Guid spaceId, AccessOperation operation,
        CancellationToken ct)
    {
        if (page is null)
        {
            return (PageOpeningState.Missing, PageAuthorization.Missing(operation));
        }
        if (page.SpaceId != spaceId)
        {
            return (PageOpeningState.Moved, PageAuthorization.Missing(operation));
        }
        if (page.IsTrashed)
        {
            return (PageOpeningState.Trashed, PageAuthorization.Missing(operation));
        }
        var decision = await m_access.AuthorizePageAsync(page, operation, ct);
        var state = decision.Outcome switch
        {
            PageAccessOutcome.Allowed => PageOpeningState.Open,
            PageAccessOutcome.Forbidden => PageOpeningState.Forbidden,
            _ => PageOpeningState.Hidden
        };
        return (state, decision);
    }

    /// <summary>No page has the id: the account is not locked for it, as in the handlers that open a page themselves.</summary>
    private static PageOpening<TPage> NoPage<TPage>(AccessOperation operation, bool tellsMove) where TPage : class
    {
        return new PageOpening<TPage>(PageOpeningState.Missing, null, PageAuthorization.Missing(operation), null, tellsMove);
    }

    /// <summary>An EditablePage says that the account may edit the page; a weaker operation would hand one to a reader.</summary>
    private static void RequireEditOperation(AccessOperation operation)
    {
        if (operation is not (AccessOperation.EditContent or AccessOperation.ManageAccess))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "An edit gate decides EditContent or ManageAccess.");
        }
    }

    private Task<Guid> SpaceOfAsync(Guid pageId, CancellationToken ct)
    {
        return m_session.QueryOver<Page>().Where(x => x.Id == pageId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
    }
}
