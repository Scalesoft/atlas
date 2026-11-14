using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Administration.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class RenameGroupHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;

    public RenameGroupHandler(ISession session, AdministrationAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<GroupSummary> Handle(RenameGroup command, CancellationToken ct)
    {
        var actor = await m_access.LockAdministrationAsync(ct);
        var current = await AdministrationReads.GroupAsync(m_session, command.GroupId, ct);
        var name = Rules.Required(command.Request.Name, "Název skupiny", unchanged: current.Name);
        await LocalGroups.RequireAsync(m_session, command.GroupId, ct);
        AdministrationVersion.ExpectFormatted(current.Version,command.Request.ExpectedVersion);
        if (await GroupNames.ExistsAsync(m_session, name, command.GroupId, ct))
        {
            throw new ApiException(409, "Skupina s tímto názvem již existuje.");
        }
        if (name != current.Name)
        {
            var group = await m_session.GetAsync<UserGroup>(command.GroupId, ct);
            group!.Name = name;
            await GroupHistory.RecordAsync(m_session, group, actor.Id, ct);
            await AdministrationReads.RefreshAsync(m_session, ct);
        }
        return await AdministrationReads.GroupAsync(m_session, command.GroupId, ct);
    }
}
