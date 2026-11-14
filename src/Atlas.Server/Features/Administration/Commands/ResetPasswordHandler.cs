using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Builtin;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class ResetPasswordHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly BuiltinAuthenticationOptions m_builtin;
    private readonly CredentialAudit m_audit;
    private readonly AccessChanges m_accessChanges;
    private readonly ISecurityChangeGuard m_guard;

    public ResetPasswordHandler(ISession session, AdministrationAccess access, IAtlasLocks locks,
        BuiltinAuthenticationOptions builtin, CredentialAudit audit, AccessChanges accessChanges, ISecurityChangeGuard guard)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_builtin = builtin;
        m_audit = audit;
        m_accessChanges = accessChanges;
        m_guard = guard;
    }

    public async Task<IssuedAccountToken> Handle(ResetPassword command, CancellationToken ct)
    {
        if (!m_builtin.Enabled)
        {
            throw new ApiException(409, "Lokální přihlášení je vypnuté.");
        }
        var actor = await m_access.LockAdministrationAsync(ct);
        await m_locks.LockAccountForUpdateAsync(command.UserId, ct);
        var current = await AdministrationReads.UserAsync(m_session, command.UserId, ct);
        AdministrationVersion.ExpectFormatted(current.Version,command.Request.ExpectedVersion);
        if (!current.HasBuiltinCredential)
        {
            throw new ApiException(409, "Účet nemá lokální přihlašovací metodu.");
        }
        await m_guard.RequireAsync(actor, new SecurityChange("account.password_reset", command.UserId), ct);
        var token = await AccountTokens.ResetAsync(m_session, command.UserId, actor.Id, ct);
        // IssueAsync rotated the security stamp: the account's live collaboration sessions end with its sign-ins.
        m_accessChanges.Changed();
        await m_audit.RecordAsync(actor.Id, command.UserId, "credential.reset_requested", ct);
        return token;
    }
}
