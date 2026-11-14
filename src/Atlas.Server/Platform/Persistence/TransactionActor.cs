using System.Runtime.CompilerServices;
using NHibernate;
using NHibernate.Engine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Persistence;

/// <summary>
/// The actor of a session's current transaction, as <see cref="AtlasLocks.SetActorAsync"/> names it: the history rows written beside
/// a row (<see cref="ContentRowEffects"/>) record it. Like the transaction-local <c>scaleatlas.actor_id</c> it replaced, it
/// ends with the transaction; a write without one (a job, an import outside a request) records none.
/// </summary>
public static class TransactionActor
{
    private static readonly ConditionalWeakTable<ISessionImplementor, Holder> s_actors = new();

    public static void Set(ISession session, Guid actorId)
    {
        var implementor = session.GetSessionImplementation();
        var holder = s_actors.GetOrCreateValue(implementor);
        holder.Transaction = implementor.ConnectionManager.CurrentTransaction;
        holder.ActorId = actorId;
    }

    public static Guid? Of(ISessionImplementor session)
    {
        return s_actors.TryGetValue(session, out var holder) && holder.Transaction is { } transaction
            && ReferenceEquals(transaction, session.ConnectionManager.CurrentTransaction) ? holder.ActorId : null;
    }

    private sealed class Holder
    {
        public ITransaction? Transaction;
        public Guid ActorId;
    }
}
