namespace Atlas.Server.Platform.Identity;

/// <summary>
/// What a gate of PageGate found, before anything is answered: the locks are taken and the page is decided, and the caller
/// chooses the answer. Most callers resolve it at once with <see cref="Require()"/>, <see cref="RequireHidden"/> or
/// <see cref="Require(string)"/>; a caller with a precedence or a wording of its own looks at <see cref="State"/> first.
/// </summary>
/// <typeparam name="TPage">LockedPage from a read gate, EditablePage from an edit gate.</typeparam>
public sealed class PageOpening<TPage> where TPage : class
{
    private readonly PageOpeningState m_state;
    private readonly UserAccount? m_actor;
    private readonly PageAuthorization m_decision;
    private readonly TPage? m_page;
    private readonly bool m_tellsMove;

    /// <param name="decision">The decision of a page that was decided; PageAuthorization.Missing for one that was not.</param>
    /// <param name="page">The opened page; null unless the state is Open.</param>
    /// <param name="tellsMove">Whether the standard answer tells that the page moved (an edit) or hides it as missing (a read).</param>
    internal PageOpening(PageOpeningState state, UserAccount? actor, PageAuthorization decision, TPage? page, bool tellsMove)
    {
        m_state = state;
        m_actor = actor;
        m_decision = decision;
        m_page = page;
        m_tellsMove = tellsMove;
    }

    public PageOpeningState State => m_state;

    /// <summary>The request's account as it is under its lock. Null when the gate stopped before it locked the account, as the
    /// handlers that open a page themselves do: no page has the id, or a restructure found the row of the page's space gone.</summary>
    public UserAccount? Actor => m_actor;

    /// <summary>
    /// The page, or the standard answer of a point-of-use page path: 404 for a missing, trashed or hidden page, 403 with the text
    /// of the operation for a forbidden one (PageAuthorization.Require). A page that moved is 409 space_changed from an edit gate,
    /// so the client reloads it, and the same 404 from a read gate.
    /// </summary>
    public TPage Require()
    {
        if (m_state == PageOpeningState.Moved && m_tellsMove)
        {
            throw new ApiException(409, "Stránka byla přesunuta. Načtěte ji znovu.", "space_changed");
        }
        m_decision.Require();
        return m_page!;
    }

    /// <summary>The page, or the 404 of a missing page for every other state: the account is told nothing, not even that the
    /// operation is refused or that the page moved (history, editor assist; docs/architecture.md 10.8).</summary>
    public TPage RequireHidden()
    {
        if (m_state != PageOpeningState.Open)
        {
            PageAuthorization.Missing(m_decision.Operation).Require();
        }
        return m_page!;
    }

    /// <summary><see cref="Require()"/> with the caller's own text for the 403 of a forbidden operation.</summary>
    public TPage Require(string forbiddenText)
    {
        if (m_state == PageOpeningState.Forbidden)
        {
            throw new ApiException(403, forbiddenText);
        }
        return Require();
    }
}
