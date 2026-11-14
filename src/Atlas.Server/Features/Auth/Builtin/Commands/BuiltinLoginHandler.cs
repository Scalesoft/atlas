using Microsoft.AspNetCore.Identity;
using Atlas.Server.Features.Auth.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Builtin.Commands;

public sealed class BuiltinLoginHandler
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly IPasswordHasher<UserAccount> m_hasher;

    public BuiltinLoginHandler(ISession session, IAtlasLocks locks, IPasswordHasher<UserAccount> hasher)
    {
        m_session = session;
        m_locks = locks;
        m_hasher = hasher;
    }

    public async Task<UserAccount> Handle(BuiltinLogin command, CancellationToken ct)
    {
        var user = await m_session.QueryOver<UserAccount>().Where(x => x.Username == command.Username).SingleOrDefaultAsync(ct);
        if (user is not null)
        {
            // Serialize verification/rehashing with password reset and account deactivation.
            // A rehash must never restore a credential that an administrator just disabled.
            await m_locks.LockAccountForUpdateAsync(user.Id, ct);
            await m_session.RefreshAsync(user, ct);
        }
        var credential = user is null ? null : await m_session.GetAsync<BuiltinCredential>(user.Id, ct);
        if (user is null || !user.IsActive || string.IsNullOrEmpty(credential?.PasswordHash))
        {
            // Spend the same work on a missing account so timing does not reveal who exists.
            m_hasher.HashPassword(new UserAccount(), command.Password);
            throw InvalidCredentials();
        }
        var verification = m_hasher.VerifyHashedPassword(user, credential.PasswordHash, command.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            throw InvalidCredentials();
        }
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            credential.PasswordHash = m_hasher.HashPassword(user, command.Password);
        }
        return user;
    }

    private static ApiException InvalidCredentials() => new(401, "Neplatné přihlašovací údaje.");
}
