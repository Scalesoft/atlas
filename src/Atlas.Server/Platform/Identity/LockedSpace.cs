using NHibernate;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// A space as PageGate entered it: the shared content lock, the space row and the request's account are locked until the
/// transaction ends, so a further page of the space is opened under the same locks with its own row lock alone. Only PageGate
/// makes one, and it is good for the transaction that entered: used after it, it throws (<see cref="RequireHeld"/>).
/// The space row is held FOR SHARE, and FOR UPDATE as well only when the space was entered for change
/// (<see cref="ForChange"/>): every opened page hands out its space, so a space alone does not say that a page may be inserted.
/// </summary>
public sealed class LockedSpace
{
    private readonly PageGate m_gate;
    private readonly Guid m_spaceId;
    private readonly UserAccount m_actor;
    private readonly ITransaction m_taken;
    private readonly bool m_forChange;

    internal LockedSpace(PageGate gate, Guid spaceId, UserAccount actor, ITransaction taken, bool forChange)
    {
        m_gate = gate;
        m_spaceId = spaceId;
        m_actor = actor;
        m_taken = taken;
        m_forChange = forChange;
    }

    public Guid SpaceId => m_spaceId;

    /// <summary>The request's account as it is under its lock: the only authorization-grade actor.</summary>
    public UserAccount Actor => m_actor;

    /// <summary>
    /// Whether the gate took the space row FOR UPDATE before its FOR SHARE when it entered: PageGate.EnterSpaceForChangeAsync
    /// and PageGate.RestructureAsync do, PageGate.EnterSpaceAsync, ReadAsync and EditAsync do not. A space entered without it
    /// says false also when an earlier entry of the same transaction took the row FOR UPDATE: the proof is the space that
    /// entry returned.
    /// </summary>
    public bool ForChange => m_forChange;

    /// <summary>
    /// Throws InvalidOperationException unless the transaction that took these locks is still the session's running one. Locks
    /// end with their transaction, so a space or a page kept beyond it proves nothing: using one later is a programming error,
    /// not something to answer a client with. The session is that of the gate that entered: what takes a page or a space as
    /// its proof and works on a session asks that session's gate before it relies on it (PageGate.RequireOwn).
    /// </summary>
    public void RequireHeld()
    {
        m_gate.RequireCurrent(m_taken);
    }

    /// <summary>Whether <paramref name="gate"/> entered this space, so that its locks are those of that gate's session
    /// (PageGate.RequireOwn).</summary>
    internal bool IsOf(PageGate gate)
    {
        return ReferenceEquals(m_gate, gate);
    }

    /// <summary>
    /// <see cref="RequireHeld"/>, and the space was entered for change. What inserts a page takes the space as its proof and
    /// requires this first: a page's insert takes the space FOR UPDATE (ContentRowEffects), and an insert under a space held only
    /// FOR SHARE has to upgrade it, on which two inserts wait for each other (40P01, c54d1c0f). Throws
    /// InvalidOperationException otherwise, as a programming error. Like <see cref="RequireHeld"/> it answers for the gate
    /// that entered: what inserts a page asks the gate of its own scope, which asks this (PageGate.RequireOwnForChange).
    /// </summary>
    public void RequireHeldForChange()
    {
        RequireHeld();
        if (!m_forChange)
        {
            throw new InvalidOperationException(
                "This space was not entered for change; enter it for change before anything else in the transaction locks it.");
        }
    }

    /// <summary>
    /// A page of this space FOR SHARE, decided for the operation: one row lock, no further space or account lock. A page of
    /// another space is Moved. Page rows are locked in ascending id order: a caller that opens several pages orders them itself,
    /// because the gate sees one call at a time.
    /// </summary>
    public Task<PageOpening<LockedPage>> ReadPageAsync(Guid pageId, AccessOperation operation, CancellationToken ct)
    {
        return m_gate.ReadPageAsync(this, pageId, operation, ct);
    }
}
