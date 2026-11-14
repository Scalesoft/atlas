using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceCovers.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceCovers.Commands;

public sealed class SetSpaceCoverTintHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;

    public SetSpaceCoverTintHandler(ISession session, IContentAccess access, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    /// <summary>The same file under a new row; the same choice again is a no-op.</summary>
    public async Task<SpaceCoverChange> Handle(SetSpaceCoverTint command, CancellationToken ct)
    {
        var (actor, space) = await SpaceCoverChanges.LockAsync(m_session, m_access, m_locks, command.SpaceId, ct);
        var current = await SpaceCoverHistory.CurrentAsync(m_session, command.SpaceId, ct);
        if (current?.FileId is null)
        {
            throw SpaceCoverChanges.Missing();
        }
        if (current.Tinted == command.Tinted)
        {
            return new SpaceCoverChange(SpaceCoverDto.From(current, space), null);
        }
        var next = new SpaceCover
        {
            Id = Guid.NewGuid(), SpaceId = current.SpaceId, Source = SpaceCoverSource.Upload, FileId = current.FileId, MimeType = current.MimeType,
            Size = current.Size, Sha256 = current.Sha256, Tinted = command.Tinted, ChangedBy = actor.Id
        };
        await SpaceCoverHistory.AdvanceAsync(m_session, current, next, ct);
        return new SpaceCoverChange(SpaceCoverDto.From(next, space), null);
    }
}
