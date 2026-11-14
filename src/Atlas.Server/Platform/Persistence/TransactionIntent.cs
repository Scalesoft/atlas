namespace Atlas.Server.Platform.Persistence;

/// <summary>
/// Whether the current message's transaction serves a read: a message declared in a <c>…Queries</c>
/// namespace. Set by <see cref="NHibernateTransactionMiddleware"/> for each message.
/// </summary>
/// <remarks>
/// Reads still take row locks FOR SHARE, which gives their transaction an id and a commit record. A read's
/// commit does not need to wait for the WAL flush, so <see cref="AtlasLocks.SetActorAsync"/> turns
/// synchronous_commit off for it in the same statement. Owner decision 2026-09-26: a crash may then lose the
/// last ~600 ms of read-side records (page access audit); the database stays consistent, and commands keep
/// fully durable commits.
/// </remarks>
public sealed class TransactionIntent
{
    public bool IsRead { get; private set; }

    public void Begin(object? message)
    {
        IsRead = message?.GetType().Namespace?.EndsWith(".Queries", StringComparison.Ordinal) == true;
    }
}
