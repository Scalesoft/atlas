using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Queries;

/// <summary>An object for the reader: 404 when it is missing, trashed, of a type no enabled module registers, or not readable
/// (no difference). The title of a page or board is its projection's (the published one), of a stored object the envelope's.</summary>
public sealed class GetObjectHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly ObjectTypes m_types;

    public GetObjectHandler(ISession session, IContentAccess access, ObjectTypes types)
    {
        m_session = session;
        m_access = access;
        m_types = types;
    }

    public async Task<ObjectDto> Handle(GetObject query, CancellationToken ct)
    {
        var spaceId = await m_session.QueryOver<ContentResource>().Where(x => x.Id == query.ObjectId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid?>(ct);
        var actor = await m_access.LockActorAsync(spaceId, ct);
        if (await m_session.GetAsync<ContentResource>(query.ObjectId, ct) is not { TrashedAt: null } envelope || m_types.Find(envelope.TypeId) is not { } type)
        {
            throw ObjectErrors.NotFound();
        }
        var access = await type.AuthorizeAsync(new ObjectActor(actor), envelope.Id, ct);
        if (access == ObjectAccess.None)
        {
            throw ObjectErrors.NotFound();
        }
        var title = envelope.Title ?? (await type.ProjectAsync(envelope.Id, ct))?.Title ?? "";
        return ObjectEnvelopes.ToDto(envelope, type, access == ObjectAccess.Write, title);
    }
}
