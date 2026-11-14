using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Administration.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class AddGroupHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;

    public AddGroupHandler(ISession session, AdministrationAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<GroupSummary> Handle(AddGroup command, CancellationToken ct)
    {
        var name = Rules.Required(command.Request.Name, "Název skupiny");
        var actor = await m_access.LockAdministrationAsync(ct);
        if (await GroupNames.ExistsAsync(m_session, name, null, ct))
        {
            throw new ApiException(409, "Skupina s tímto názvem již existuje.");
        }
        var id = Guid.NewGuid();
        var group = new UserGroup { Id = id, Name = name };
        await m_session.SaveAsync(group, ct);
        await GroupHistory.RecordAsync(m_session, group, actor.Id, ct);
        await AdministrationReads.RefreshAsync(m_session, ct);
        return await AdministrationReads.GroupAsync(m_session, id, ct);
    }
}
