using NHibernate;
using Atlas.Server.Features.Auth.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Commands;

public sealed class RecordSignInHandler
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;

    public RecordSignInHandler(ISession session, IAtlasLocks locks)
    {
        m_session = session;
        m_locks = locks;
    }

    public async Task<DateTime> Handle(RecordSignIn command, CancellationToken ct)
    {
        // Serialize with password reset, recovery and deactivation: a sign-in fact must never be
        // written for a stamp that another transaction has just rotated.
        if (!await m_locks.LockAccountForUpdateAsync(command.UserId, ct))
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        var account = await m_session.GetAsync<UserAccount>(command.UserId, ct);
        if (account is null || !account.IsActive || account.SecurityStamp != command.SecurityStamp)
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        // PostgreSQL timestamps hold microseconds while DateTime counts ticks. Truncate so the value
        // returned to the caller is the value the row keeps and the two can be compared.
        var authenticatedAt = Temporal.TruncateToMicroseconds(DateTime.UtcNow);
        await m_session.SaveAsync(new UserSignIn
        {
            Id = Guid.NewGuid(),
            UserId = account.Id,
            AuthenticatedAt = authenticatedAt,
            AuthenticationMethod = command.Method
        }, ct);
        // The newest 100 per account stay: OperationalRetention (UserSignIn) trims the rest hourly.
        await m_session.FlushAsync(ct);
        return authenticatedAt;
    }
}
