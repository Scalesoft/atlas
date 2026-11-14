namespace Atlas.Server.Platform.Identity;

/// <summary>
/// A page as a read gate of PageGate opened it: its row is locked at least FOR SHARE until the transaction ends, the instance is
/// the row as it is under that lock, the page is outside the trash and in the space locked for it, and the account may perform
/// the operation the gate was asked for. Only PageGate makes one, so holding it is the proof, for the transaction that opened it.
/// </summary>
public sealed class LockedPage
{
    private readonly Page m_page;
    private readonly LockedSpace m_space;
    private readonly AccessOperation m_operation;
    private readonly bool m_canEdit;

    internal LockedPage(Page page, LockedSpace space, AccessOperation operation, bool canEdit)
    {
        m_page = page;
        m_space = space;
        m_operation = operation;
        m_canEdit = canEdit;
    }

    public Page Page => m_page;

    /// <summary>The space the page was opened under: a further page of it is read under the same locks
    /// (<see cref="LockedSpace.ReadPageAsync"/>), without entering the space again.</summary>
    public LockedSpace Space => m_space;

    /// <summary>The request's account as it is under its lock: the only authorization-grade actor.</summary>
    public UserAccount Actor => m_space.Actor;

    /// <summary>The operation the gate decided and allowed: what takes the page and needs more than this must not rely on it.</summary>
    public AccessOperation Operation => m_operation;

    /// <summary>Whether the account may also edit the page's working content (PageAuthorization.CanEdit): an editor reads the
    /// working revision, anyone else the publication.</summary>
    public bool CanEdit => m_canEdit;

    /// <summary>Throws InvalidOperationException unless the transaction that opened the page is still the session's running one
    /// (<see cref="LockedSpace.RequireHeld"/>).</summary>
    public void RequireHeld()
    {
        m_space.RequireHeld();
    }
}
