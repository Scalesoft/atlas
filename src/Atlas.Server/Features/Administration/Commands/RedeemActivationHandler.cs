using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Recovery.Domain;
using Atlas.Server.Features.Auth.Domain;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class RedeemActivationHandler
{
    private const int TokenLength = 64;
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly IPasswordHasher<UserAccount> m_hasher;
    private readonly BuiltinAuthenticationOptions m_builtin;
    private readonly BuiltinPasswordPolicy m_policy;
    private readonly CredentialAudit m_audit;
    private readonly AccessChanges m_accessChanges;

    public RedeemActivationHandler(ISession session, IAtlasLocks locks, IPasswordHasher<UserAccount> hasher,
        BuiltinAuthenticationOptions builtin, BuiltinPasswordPolicy policy, CredentialAudit audit, AccessChanges accessChanges)
    {
        m_session = session;
        m_locks = locks;
        m_hasher = hasher;
        m_builtin = builtin;
        m_policy = policy;
        m_audit = audit;
        m_accessChanges = accessChanges;
    }

    public async Task Handle(RedeemActivation command, CancellationToken ct)
    {
        var request = command.Request;
        if (!m_builtin.Enabled)
        {
            throw new ApiException(409, "Lokální přihlášení je vypnuté.");
        }
        m_policy.Validate(request.NewPassword);
        if (string.IsNullOrEmpty(request.Token) || request.Token.Length != TokenLength)
        {
            throw new ApiException(400, "Aktivační odkaz je neplatný nebo vypršel.");
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Token));
        var found = await m_session.QueryOver<BuiltinAccountToken>().Where(x => x.TokenHash == hash).SingleOrDefaultAsync(ct)
            ?? throw new ApiException(400, "Aktivační odkaz je neplatný nebo vypršel.");
        var userId = found.UserId;
        await m_locks.LockAccountForUpdateAsync(userId, ct);
        // The winner of a concurrent redeem committed while we waited on the account lock; the identity map still holds its stale copy.
        m_session.Clear();
        var token = await m_session.QueryOver<BuiltinAccountToken>().Where(x => x.TokenHash == hash).SingleOrDefaultAsync(ct);
        var user = await m_session.GetAsync<UserAccount>(userId, ct);
        var credential = await m_session.GetAsync<BuiltinCredential>(userId, ct);
        if (token is null || token.ConsumedAt is not null || token.ExpiresAt <= DateTime.UtcNow
            || user is null || !user.IsActive || credential is null)
        {
            throw new ApiException(400, "Aktivační odkaz je neplatný nebo vypršel.");
        }
        credential.PasswordHash = m_hasher.HashPassword(new UserAccount { Id = userId }, request.NewPassword);
        token.ConsumedAt = DateTime.UtcNow;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        m_accessChanges.Changed();
        // Anonymous: the activation link is the only proof, nobody is signed in.
        await AccountHistory.RecordAsync(m_session, user, null, ct);
        await m_audit.RecordAsync(null, userId, "credential.activated", ct);
    }
}
