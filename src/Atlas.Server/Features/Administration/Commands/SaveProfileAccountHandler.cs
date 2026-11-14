using System.Net.Mail;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class SaveProfileAccountHandler
{
    private const int MaxEmailLength = 254;
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;
    private readonly ICurrentActor m_currentActor;
    private readonly IAtlasLocks m_locks;

    public SaveProfileAccountHandler(ISession session, AdministrationAccess access, ICurrentActor currentActor, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_currentActor = currentActor;
        m_locks = locks;
    }

    public async Task<ProfileAccount> Handle(SaveProfileAccount command, CancellationToken ct)
    {
        var request = command.Request;
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        if (email is not null && (email.Length > MaxEmailLength || email.Any(char.IsWhiteSpace) || email.Any(char.IsControl)
            || !MailAddress.TryCreate(email, out var address) || address.Address != email))
        {
            throw new ApiException(400, "Zadejte platný e-mail.");
        }
        var claimed = await m_currentActor.RequireAsync(ct);
        var name = Rules.Required(request.DisplayName, "Jméno", unchanged: claimed.DisplayName);
        // Same lock order as every administration write: the content lock before the row.
        await m_locks.AcquireContentSharedAsync(ct);
        await m_locks.LockAccountForUpdateAsync(claimed.Id, ct);
        var actor = await m_access.LockActorAsync(ct);
        var current = await ProfileAccountReader.ReadAsync(m_session, actor.Id, command.AuthTime, command.AuthenticationMethod, ct);
        AdministrationVersion.ExpectFormatted(current.Version,request.ExpectedVersion);
        if (current.DisplayName != name || current.Email != email)
        {
            actor.DisplayName = name;
            actor.Email = email;
            await AccountHistory.RecordAsync(m_session, actor, actor.Id, ct);
            await AdministrationReads.RefreshAsync(m_session, ct);
        }
        // RefreshAsync cleared the identity map, so the actor instance is detached; claimed.Id is the same value.
        return await ProfileAccountReader.ReadAsync(m_session, claimed.Id, command.AuthTime, command.AuthenticationMethod, ct);
    }
}
