using NHibernate;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Persistence;

/// <summary>Wolverine's envelopes written in the NHibernate transaction of a message (3.3 plan §2): a rollback leaves none, and after the
/// commit Wolverine hands them to their queue at once (the flush after the handler), without polling.</summary>
/// <remarks>The rollback belongs to <see cref="NHibernateTransactionMiddleware"/>, which disposes the transaction. Wolverine's own
/// RollbackAsync after a failed handler would reach the transaction object of a connection NHibernate has already returned to the pool,
/// and Npgsql reuses that object for whichever session rents the connection next.</remarks>
internal sealed class NHibernateEnvelopeTransaction(DatabaseEnvelopeTransaction inner) : IEnvelopeTransaction
{
    /// <summary>Enlists <paramref name="messages"/> in <paramref name="transaction"/>; nothing without a message store (a host that
    /// routes no durable message, see JobQueue).</summary>
    public static Task EnlistAsync(ISession session, ITransaction transaction, IMessageContext messages)
    {
        if (messages is not MessageContext { Storage: IMessageDatabase database } context)
        {
            return Task.CompletedTask;
        }
        // NHibernate keeps its DbTransaction to itself; enlisting a command hands it out.
        using var command = session.Connection.CreateCommand();
        transaction.Enlist(command);
        return context.EnlistInOutboxAsync(new NHibernateEnvelopeTransaction(new DatabaseEnvelopeTransaction(database, command.Transaction!)));
    }

    public Task PersistOutgoingAsync(Envelope envelope) => inner.PersistOutgoingAsync(envelope);

    public Task PersistOutgoingAsync(Envelope[] envelopes) => inner.PersistOutgoingAsync(envelopes);

    public Task PersistIncomingAsync(Envelope envelope) => inner.PersistIncomingAsync(envelope);

    public ValueTask RollbackAsync() => ValueTask.CompletedTask;

    public Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, DurabilitySettings settings, CancellationToken cancellation)
        => inner.TryMakeEagerIdempotencyCheckAsync(envelope, settings, cancellation);
}
