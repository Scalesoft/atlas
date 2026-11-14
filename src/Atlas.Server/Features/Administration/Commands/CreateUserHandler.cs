using NHibernate;
using Npgsql;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class CreateUserHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;
    private readonly BuiltinAuthenticationOptions m_builtin;
    private readonly ISecurityChangeGuard m_guard;
    private readonly IJobRequests m_jobs;

    public CreateUserHandler(ISession session, AdministrationAccess access, BuiltinAuthenticationOptions builtin, ISecurityChangeGuard guard, IJobRequests jobs)
    {
        m_session = session;
        m_access = access;
        m_builtin = builtin;
        m_guard = guard;
        m_jobs = jobs;
    }

    public async Task<CreatedBuiltinUser> Handle(CreateUser command, CancellationToken ct)
    {
        if (!m_builtin.Enabled)
        {
            throw new ApiException(409, "Lokální přihlášení je vypnuté.");
        }
        var name = Rules.Required(command.Request.DisplayName, "Jméno");
        var username = Rules.Required(command.Request.Username, "Uživatelské jméno", 100).ToLowerInvariant();
        var actor = await m_access.LockAdministrationAsync(ct);
        var id = Guid.NewGuid();
        // A new account created as WikiAdmin turns the role on, as SaveUser does for an existing one.
        if (command.Request.IsWikiAdmin)
        {
            await m_guard.RequireAsync(actor, new SecurityChange("account.wiki_admin", id), ct);
        }
        var account = new UserAccount
        {
            Id = id,
            Username = username,
            DisplayName = name,
            IsActive = true,
            IsWikiAdmin = command.Request.IsWikiAdmin,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };
        await m_session.SaveAsync(account, ct);
        await m_session.SaveAsync(new BuiltinCredential { UserId = id, PasswordHash = null }, ct);
        try
        {
            await m_session.FlushAsync(ct);
        }
        catch (ADOException error) when (error.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new ApiException(409, "Uživatelské jméno už existuje.");
        }
        await AccountHistory.RecordAsync(m_session, account, actor.Id, ct, created: true);
        await m_jobs.RequestAsync(AccountContactsJob.JobId, "Nový účet", ct: ct);
        var token = await AccountTokens.InviteAsync(m_session, id, actor.Id, ct);
        await AdministrationReads.RefreshAsync(m_session, ct);
        var user = await AdministrationReads.UserAsync(m_session, id, ct);
        return new CreatedBuiltinUser(user, token.Token, token.ExpiresAt, token.Purpose);
    }
}
