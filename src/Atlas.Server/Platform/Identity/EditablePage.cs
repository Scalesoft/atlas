namespace Atlas.Server.Platform.Identity;

/// <summary>
/// A page as an edit gate of PageGate opened it: its row is locked FOR UPDATE until the transaction ends, the instance is the
/// row as it is under that lock, the page is outside the trash and in the space locked for it, and the account may perform the
/// operation the gate was asked for, EditContent or ManageAccess. A type of its own, not a LockedPage with a flag: what writes
/// a page takes this, so a page that is only locked for reading cannot be passed to it. Only PageGate makes one, and it is
/// good for the transaction that opened it, on the session of the gate that opened it: what takes one asks the gate of its
/// own scope first (PageGate.RequireOwn).
/// </summary>
public sealed class EditablePage
{
    private readonly Page m_page;
    private readonly LockedSpace m_space;
    private readonly AccessOperation m_operation;

    internal EditablePage(Page page, LockedSpace space, AccessOperation operation)
    {
        m_page = page;
        m_space = space;
        m_operation = operation;
    }

    public Page Page => m_page;

    /// <summary>The space the page was opened under: a further page of it is read under the same locks
    /// (<see cref="LockedSpace.ReadPageAsync"/>), without entering the space again.</summary>
    public LockedSpace Space => m_space;

    /// <summary>The request's account as it is under its lock: the only authorization-grade actor.</summary>
    public UserAccount Actor => m_space.Actor;

    /// <summary>The operation the gate decided and allowed, EditContent or ManageAccess: what takes the page and needs
    /// ManageAccess must not rely on one opened for EditContent.</summary>
    public AccessOperation Operation => m_operation;

    /// <summary>Throws InvalidOperationException unless the transaction that opened the page is still the running one of the
    /// session it was opened on (<see cref="LockedSpace.RequireHeld"/>). That is the session of the gate that opened it: what
    /// works on a session asks that session's gate instead (PageGate.RequireOwn).</summary>
    public void RequireHeld()
    {
        m_space.RequireHeld();
    }
}
