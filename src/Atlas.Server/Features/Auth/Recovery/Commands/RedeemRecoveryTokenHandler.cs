using Microsoft.AspNetCore.Identity;
using NHibernate.Criterion;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Persistence;
using Atlas.Server.Features.Auth.Recovery.Domain;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Recovery.Commands;

public sealed class RedeemRecoveryTokenHandler
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly IPasswordHasher<UserAccount> m_hasher;
    private readonly CredentialAudit m_audit;
    private readonly AccessChanges m_accessChanges;
    private readonly IJobRequests m_jobs;

    public RedeemRecoveryTokenHandler(ISession session, IAtlasLocks locks, IPasswordHasher<UserAccount> hasher, CredentialAudit audit,
        AccessChanges accessChanges, IJobRequests jobs)
    {
        m_session = session;
        m_locks = locks;
        m_hasher = hasher;
        m_audit = audit;
        m_accessChanges = accessChanges;
        m_jobs = jobs;
    }

    public async Task<UserAccount> Handle(RedeemRecoveryToken command, CancellationToken ct)
    {
        await m_locks.AcquireAdvisoryAsync(RecoveryLock.Key, ct);
        var now = await DatabaseClock.NowAsync(m_session, ct);
        var token = await m_session.QueryOver<ServerRecoveryToken>()
            .Where(Restrictions.Eq(nameof(ServerRecoveryToken.TokenHash), ServerAccessTokens.Hash(command.Token)))
            .And(t => t.ConsumedAt == null && t.ExpiresAt > now)
            .SingleOrDefaultAsync(ct) ?? throw ServerAccessTokens.InvalidRecovery();
        if (!await m_locks.LockAccountForUpdateAsync(token.UserId, ct))
        {
            throw ServerAccessTokens.InvalidRecovery();
        }
        var user = await m_session.GetAsync<UserAccount>(token.UserId, ct);
        if (user is null)
        {
            throw ServerAccessTokens.InvalidRecovery();
        }
        if (!user.IsActive)
        {
            // The sweep gives contacts only to active accounts.
            await m_jobs.RequestAsync(AccountContactsJob.JobId, "Obnovený účet", ct: ct);
        }
        user.IsActive = true;
        user.IsWikiAdmin = true;
        // Server-authorized recovery grants a manual role, independent of external claim mapping.
        user.OidcAdminIssuer = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        m_accessChanges.Changed();
        // Anonymous: the server's recovery token is the only proof, nobody is signed in.
        await AccountHistory.RecordAsync(m_session, user, null, ct);
        var credential = await m_session.GetAsync<BuiltinCredential>(user.Id, ct);
        if (credential is null)
        {
            credential = new BuiltinCredential { UserId = user.Id };
            await m_session.SaveAsync(credential, ct);
        }
        credential.PasswordHash = m_hasher.HashPassword(user, command.NewPassword);
        await m_session.CreateQuery("update BuiltinAccountToken set ConsumedAt = :now where UserId = :user and ConsumedAt is null")
            .SetParameter("now", now)
            .SetParameter("user", user.Id)
            .ExecuteUpdateAsync(ct);
        token.ConsumedAt = now;
        await m_audit.RecordAsync(null, user.Id, "credential.recovered", ct);
        return user;
    }
}
