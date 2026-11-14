using Microsoft.AspNetCore.Identity;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Recovery.Domain;
using Atlas.Server.Features.Auth.Domain;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class ChangePasswordHandler
{
    private const int MaxPasswordLength = 256;
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;
    private readonly ICurrentActor m_currentActor;
    private readonly IAtlasLocks m_locks;
    private readonly IPasswordHasher<UserAccount> m_hasher;
    private readonly BuiltinAuthenticationOptions m_builtin;
    private readonly BuiltinPasswordPolicy m_policy;
    private readonly CredentialAudit m_audit;
    private readonly AccessChanges m_accessChanges;
    private readonly BuiltinLoginThrottle m_throttle;

    public ChangePasswordHandler(ISession session, AdministrationAccess access, ICurrentActor currentActor, IAtlasLocks locks,
        IPasswordHasher<UserAccount> hasher, BuiltinAuthenticationOptions builtin, BuiltinPasswordPolicy policy, CredentialAudit audit,
        AccessChanges accessChanges, BuiltinLoginThrottle throttle)
    {
        m_session = session;
        m_access = access;
        m_currentActor = currentActor;
        m_locks = locks;
        m_hasher = hasher;
        m_builtin = builtin;
        m_policy = policy;
        m_audit = audit;
        m_accessChanges = accessChanges;
        m_throttle = throttle;
    }

    public async Task<ChangedPassword> Handle(ChangePassword command, CancellationToken ct)
    {
        var request = command.Request;
        if (!m_builtin.Enabled)
        {
            throw new ApiException(409, "Lokální přihlášení je vypnuté.");
        }
        m_policy.Validate(request.NewPassword);
        if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > MaxPasswordLength)
        {
            throw new ApiException(400, "Současné heslo není správné.");
        }
        // Claims only give the row to lock; every decision below uses the account read back under that lock.
        var claimed = await m_currentActor.RequireAsync(ct);
        // Same lock order as every administration write: the content lock before the row, or this deadlocks
        // against SaveUser and ResetPassword on the same account. LockActorAsync re-acquiring it is a no-op.
        await m_locks.AcquireContentSharedAsync(ct);
        await m_locks.LockAccountForUpdateAsync(claimed.Id, ct);
        var actor = await m_access.LockActorAsync(ct);
        var credential = await m_session.GetAsync<BuiltinCredential>(actor.Id, ct);
        if (string.IsNullOrEmpty(credential?.PasswordHash))
        {
            throw new ApiException(400, "Současné heslo není správné nebo lokální přihlášení není aktivní.");
        }
        using var attempt = m_throttle.Begin(actor.Username);
        if (m_hasher.VerifyHashedPassword(actor, credential.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            attempt.Fail();
            throw new ApiException(400, "Současné heslo není správné nebo lokální přihlášení není aktivní.");
        }
        var stamp = Guid.NewGuid().ToString("N");
        credential.PasswordHash = m_hasher.HashPassword(actor, request.NewPassword);
        await m_session.CreateQuery("delete BuiltinAccountToken where UserId = :user")
            .SetParameter("user", actor.Id)
            .ExecuteUpdateAsync(ct);
        actor.SecurityStamp = stamp;
        m_accessChanges.Changed();
        await AccountHistory.RecordAsync(m_session, actor, actor.Id, ct);
        await m_audit.RecordAsync(actor.Id, actor.Id, "credential.password_changed", ct);
        return new ChangedPassword(actor.Id, actor.Username, actor.DisplayName, actor.IsWikiAdmin, stamp);
    }
}
