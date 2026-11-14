using NHibernate.Criterion;
using Atlas.Server.Features.Auth.Persistence;
using Atlas.Server.Features.Auth.Recovery.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Recovery.Commands;

public sealed class EnsureRecoveryTokenHandler
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly ServerTokenFiles m_files;
    private readonly RecoveryOptions m_options;

    public EnsureRecoveryTokenHandler(ISession session, IAtlasLocks locks, ServerTokenFiles files, RecoveryOptions options)
    {
        m_session = session;
        m_locks = locks;
        m_files = files;
        m_options = options;
    }

    public async Task<RecoveryTokenResult> Handle(EnsureRecoveryToken command, CancellationToken ct)
    {
        await m_locks.AcquireAdvisoryAsync(RecoveryLock.Key, ct);
        if (await m_session.GetAsync<Workspace>(1, ct) is null)
        {
            return RecoveryTokenResult.None;
        }
        var current = await m_files.ReadRecoveryAsync(ct);
        var configuredUsername = m_options.Username?.Trim().ToLowerInvariant();
        UserAccount? found;
        if (!string.IsNullOrEmpty(configuredUsername))
        {
            found = await m_session.QueryOver<UserAccount>().Where(u => u.Username == configuredUsername).SingleOrDefaultAsync(ct);
        }
        else if (current is not null)
        {
            found = await m_session.GetAsync<UserAccount>(current.UserId, ct);
        }
        else
        {
            found = await m_session.QueryOver<UserAccount>().Where(u => u.IsWikiAdmin).OrderBy(u => u.Username).Asc.Take(1).SingleOrDefaultAsync(ct);
        }
        // A configured or previously bound account is never silently replaced by another admin.
        if (found is null)
        {
            return RecoveryTokenResult.None;
        }
        var user = found;
        var now = await DatabaseClock.NowAsync(m_session, ct);
        if (current is not null && current.UserId == user.Id && current.Token.Length == 64)
        {
            var valid = await m_session.QueryOver<ServerRecoveryToken>()
                .Where(Restrictions.Eq(nameof(ServerRecoveryToken.TokenHash), ServerAccessTokens.Hash(current.Token)))
                .And(t => t.UserId == user.Id && t.ConsumedAt == null && t.ExpiresAt > now)
                .RowCountAsync(ct);
            if (valid == 1)
            {
                return new RecoveryTokenResult(current, false);
            }
        }
        var token = ServerAccessTokens.GenerateToken();
        var next = new RecoveryTokenFile(token, user.Id, user.Username, now.AddDays(7));
        // Consume before inserting: only one unconsumed row may exist (UX_ServerRecoveryToken_Current).
        await m_session.CreateQuery("update ServerRecoveryToken set ConsumedAt = :now where ConsumedAt is null")
            .SetParameter("now", now)
            .ExecuteUpdateAsync(ct);
        await m_session.SaveAsync(new ServerRecoveryToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = ServerAccessTokens.Hash(token),
            IssuedAt = now,
            ExpiresAt = next.ExpiresAt
        }, ct);
        await m_session.FlushAsync(ct);
        // Write before commit. An interrupted write cannot create a usable capability without
        // a committed hash; the next initialization reconciles an uncommitted file safely.
        await m_files.WriteRecoveryAsync(next, ct);
        return new RecoveryTokenResult(next, true);
    }
}
