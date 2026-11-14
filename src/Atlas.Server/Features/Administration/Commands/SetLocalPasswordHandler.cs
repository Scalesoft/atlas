using Microsoft.AspNetCore.Identity;
using NHibernate;
using Npgsql;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Domain;

namespace Atlas.Server.Features.Administration.Commands;

/// <summary>
/// Adds local sign-in to an account without a password (a Hub account, or an invitation not yet redeemed), so the same account
/// signs in either way. A fresh sign-in proves it instead of a current password; other sessions stay valid, since nothing was taken
/// away. A pending invitation code dies with it.
/// </summary>
public sealed class SetLocalPasswordHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;
    private readonly ICurrentActor m_currentActor;
    private readonly IAtlasLocks m_locks;
    private readonly IPasswordHasher<UserAccount> m_hasher;
    private readonly BuiltinAuthenticationOptions m_builtin;
    private readonly BuiltinPasswordPolicy m_policy;
    private readonly CredentialAudit m_audit;
    private readonly ISecurityChangeGuard m_guard;

    public SetLocalPasswordHandler(ISession session, AdministrationAccess access, ICurrentActor currentActor, IAtlasLocks locks,
        IPasswordHasher<UserAccount> hasher, BuiltinAuthenticationOptions builtin, BuiltinPasswordPolicy policy, CredentialAudit audit,
        ISecurityChangeGuard guard)
    {
        m_session = session;
        m_access = access;
        m_currentActor = currentActor;
        m_locks = locks;
        m_hasher = hasher;
        m_builtin = builtin;
        m_policy = policy;
        m_audit = audit;
        m_guard = guard;
    }

    public async Task Handle(SetLocalPassword command, CancellationToken ct)
    {
        var request = command.Request;
        if (!m_builtin.Enabled)
        {
            throw new ApiException(409, "Lokální přihlášení je vypnuté.");
        }
        m_policy.Validate(request.NewPassword);
        // Same lock order as ChangePasswordHandler: the content lock, then the account row.
        var claimed = await m_currentActor.RequireAsync(ct);
        await m_locks.AcquireContentSharedAsync(ct);
        await m_locks.LockAccountForUpdateAsync(claimed.Id, ct);
        var actor = await m_access.LockActorAsync(ct);
        var credential = await m_session.GetAsync<BuiltinCredential>(actor.Id, ct);
        if (!string.IsNullOrEmpty(credential?.PasswordHash))
        {
            throw new ApiException(409, "Účet už lokální heslo má. Použijte změnu hesla.");
        }
        await m_guard.RequireAsync(actor, new SecurityChange("account.local_password", actor.Id), ct);
        if (UserAccount.HasPlaceholderUsername(actor.Username))
        {
            var username = Rules.Required(request.Username, "Přihlašovací jméno", 100).ToLowerInvariant();
            if (UserAccount.HasPlaceholderUsername(username))
            {
                throw new ApiException(400, "Zvolte jiné přihlašovací jméno.");
            }
            actor.Username = username;
        }
        if (credential is null)
        {
            credential = new BuiltinCredential { UserId = actor.Id };
            await m_session.SaveAsync(credential, ct);
        }
        credential.PasswordHash = m_hasher.HashPassword(actor, request.NewPassword);
        await m_session.CreateQuery("delete BuiltinAccountToken where UserId = :user")
            .SetParameter("user", actor.Id)
            .ExecuteUpdateAsync(ct);
        try
        {
            // Flushes the account first: a taken sign-in name fails here.
            await AccountHistory.RecordAsync(m_session, actor, actor.Id, ct);
            await m_session.FlushAsync(ct);
        }
        catch (ADOException error) when (error.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new ApiException(409, "Přihlašovací jméno už používá jiný účet.");
        }
        // shortcut: logged as a password change (the audit action list is a DB check); add credential.password_set by migration if the log must tell them apart.
        await m_audit.RecordAsync(actor.Id, actor.Id, "credential.password_changed", ct);
    }
}
