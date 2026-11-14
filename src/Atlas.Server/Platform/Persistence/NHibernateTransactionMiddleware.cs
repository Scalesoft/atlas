using Atlas.Server.Platform.Events;
using NHibernate;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Persistence;

/// <summary>One transaction per Wolverine message on the scoped session. Handlers never begin, commit or dispose transactions.</summary>
/// <remarks>Right before the commit it sends the handler's outbox messages (<see cref="Outbox"/>); after a successful commit it awaits
/// the live collaboration refresh a handler requested through <see cref="AccessChanges"/>. Wolverine messages the handler publishes, the
/// outbox's among them, are written in the same transaction (<see cref="NHibernateEnvelopeTransaction"/>). A job's steps run in the same
/// pattern outside a handler (Jobs.JobRunner).</remarks>
public sealed class NHibernateTransactionMiddleware
{
    private readonly ISession m_session;
    private readonly TransactionIntent m_intent;
    private readonly AccessChanges m_accessChanges;
    private readonly Outbox m_outbox;
    private ITransaction? m_transaction;

    public NHibernateTransactionMiddleware(ISession session, TransactionIntent intent, AccessChanges accessChanges, Outbox outbox)
    {
        m_session = session;
        m_intent = intent;
        m_accessChanges = accessChanges;
        m_outbox = outbox;
    }

    public Task BeforeAsync(Envelope envelope, IMessageContext messages)
    {
        m_intent.Begin(envelope.Message);
        m_accessChanges.Discard();
        m_outbox.Begin(messages);
        m_transaction = m_session.BeginTransaction();
        return NHibernateEnvelopeTransaction.EnlistAsync(m_session, m_transaction, messages);
    }

    public async Task AfterAsync(CancellationToken ct)
    {
        if (m_transaction is { IsActive: true } transaction)
        {
            await m_outbox.WriteAsync();
            await transaction.CommitAsync(ct);
            await m_accessChanges.RefreshAsync();
        }
    }

    public void Finally()
    {
        // Disposing an uncommitted transaction rolls it back.
        m_transaction?.Dispose();
        m_transaction = null;
    }
}
