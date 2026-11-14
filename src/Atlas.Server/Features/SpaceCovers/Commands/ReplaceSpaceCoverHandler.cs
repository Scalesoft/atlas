using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceCovers.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceCovers.Commands;

public sealed class ReplaceSpaceCoverHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly AttachmentFiles m_files;

    public ReplaceSpaceCoverHandler(ISession session, IContentAccess access, IAtlasLocks locks, AttachmentFiles files)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_files = files;
    }

    /// <summary>Stores the new image and makes it current. The first image is tinted; a replacement keeps the current choice.</summary>
    public async Task<SpaceCoverChange> Handle(ReplaceSpaceCover command, CancellationToken ct)
    {
        var (actor, space) = await SpaceCoverChanges.LockAsync(m_session, m_access, m_locks, command.SpaceId, ct);
        var mimeType = SpaceCoverImage.Validate(command.Content);
        var current = await SpaceCoverHistory.CurrentAsync(m_session, command.SpaceId, ct);
        var fileId = Guid.NewGuid();
        try
        {
            using var input = new MemoryStream(command.Content, false);
            var (size, hash, _) = await m_files.WriteAsync(fileId, input, true, ct);
            var next = new SpaceCover
            {
                Id = Guid.NewGuid(), SpaceId = command.SpaceId, Source = SpaceCoverSource.Upload, FileId = fileId, MimeType = mimeType,
                Size = size, Sha256 = hash, Tinted = current?.FileId is null || current.Tinted, ChangedBy = actor.Id
            };
            await SpaceCoverHistory.AdvanceAsync(m_session, current, next, ct);
            return new SpaceCoverChange(SpaceCoverDto.From(next, space), current?.FileId);
        }
        catch
        {
            // Not committed yet, so the new file is provably unreferenced. A failing COMMIT leaves an orphan, as with attachments.
            m_files.Remove(fileId);
            throw;
        }
    }
}
