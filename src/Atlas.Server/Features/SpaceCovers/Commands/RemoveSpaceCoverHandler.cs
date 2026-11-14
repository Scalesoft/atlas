using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceCovers.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceCovers.Commands;

public sealed class RemoveSpaceCoverHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;

    public RemoveSpaceCoverHandler(ISession session, IContentAccess access, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    /// <summary>Returns the space to the automatic image from any source and releases an uploaded file. Idempotent, like revoking
    /// a revoked token: a space whose image is automatic stays as it is.</summary>
    public async Task<SpaceCoverChange> Handle(RemoveSpaceCover command, CancellationToken ct)
    {
        var (actor, space) = await SpaceCoverChanges.LockAsync(m_session, m_access, m_locks, command.SpaceId, ct);
        var released = await SpaceCoverHistory.ChooseAsync(m_session, command.SpaceId, SpaceCoverSource.Auto, null, actor.Id, ct);
        return new SpaceCoverChange(SpaceCoverDto.From(null, space), released);
    }
}
