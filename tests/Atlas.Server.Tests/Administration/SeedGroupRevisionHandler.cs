using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.Administration;

public sealed class SeedGroupRevisionHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public SeedGroupRevisionHandler(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task Handle(SeedGroupRevision message, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(null, ct);
        await m_session.SaveAsync(new UserGroup { Id = message.GroupId, Name = message.Name }, ct);
        var group = await m_session.GetAsync<UserGroup>(message.GroupId, ct);
        await GroupHistory.RecordAsync(m_session, group, actor.Id, ct);
        group.Name = message.RenamedTo;
        await GroupHistory.RecordAsync(m_session, group, actor.Id, ct);
    }
}
