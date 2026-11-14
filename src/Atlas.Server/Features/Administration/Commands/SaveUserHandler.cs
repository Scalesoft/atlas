using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class SaveUserHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly AccessChanges m_accessChanges;
    private readonly ISecurityChangeGuard m_guard;
    private readonly IJobRequests m_jobs;

    public SaveUserHandler(ISession session, AdministrationAccess access, IAtlasLocks locks, AccessChanges accessChanges, ISecurityChangeGuard guard, IJobRequests jobs)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_accessChanges = accessChanges;
        m_guard = guard;
        m_jobs = jobs;
    }

    public async Task<UserSummary> Handle(SaveUser command, CancellationToken ct)
    {
        var request = command.Request;
        var actor = await m_access.LockAdministrationAsync(ct);
        await m_locks.LockAccountForUpdateAsync(command.UserId, ct);
        var current = await AdministrationReads.UserAsync(m_session, command.UserId, ct);
        var name = Rules.Required(request.DisplayName, "Jméno", unchanged: current.DisplayName);
        AdministrationVersion.ExpectFormatted(current.Version,request.ExpectedVersion);
        if (current.IsActive && current.IsWikiAdmin && !(request.IsActive && request.IsWikiAdmin))
        {
            var remaining = await m_session.QueryOver<UserAccount>()
                .Where(x => x.IsActive && x.IsWikiAdmin)
                .RowCountAsync(ct);
            if (remaining <= 1)
            {
                throw new ApiException(409, "Nelze odebrat nebo deaktivovat posledního aktivního správce wiki.");
            }
        }
        // Turning WikiAdmin on or off is a security change (owner decision 3a); a rename or (de)activation alone is not.
        if (current.IsWikiAdmin != request.IsWikiAdmin)
        {
            await m_guard.RequireAsync(actor, new SecurityChange("account.wiki_admin", command.UserId), ct);
        }
        if (current.DisplayName != name || current.IsActive != request.IsActive || current.IsWikiAdmin != request.IsWikiAdmin)
        {
            var user = await m_session.GetAsync<UserAccount>(command.UserId, ct);
            // Only a privilege or activation change invalidates existing sessions; a rename must not sign the user out.
            if (user!.IsActive != request.IsActive || user.IsWikiAdmin != request.IsWikiAdmin)
            {
                user.SecurityStamp = Guid.NewGuid().ToString("N");
                m_accessChanges.Changed();
            }
            if (!user.IsActive && request.IsActive)
            {
                // The sweep gives contacts only to active accounts.
                await m_jobs.RequestAsync(AccountContactsJob.JobId, "Obnovený účet", ct: ct);
            }
            user.DisplayName = name;
            user.IsActive = request.IsActive;
            if (user.IsWikiAdmin != request.IsWikiAdmin)
            {
                user.OidcAdminIssuer = null;
            }
            user.IsWikiAdmin = request.IsWikiAdmin;
            await AccountHistory.RecordAsync(m_session, user, actor.Id, ct);
            await AdministrationReads.RefreshAsync(m_session, ct);
        }
        return await AdministrationReads.UserAsync(m_session, command.UserId, ct);
    }
}
