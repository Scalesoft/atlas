using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceCovers.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceCovers.Queries;

public sealed class OpenSpaceCoverHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly PageListing m_listing;

    public OpenSpaceCoverHandler(ISession session, IContentAccess access, PageListing listing)
    {
        m_session = session;
        m_access = access;
        m_listing = listing;
    }

    /// <summary>Whoever has the space in the space list (ListedSpaces: a space role, or a listed page in it) sees its card and the
    /// image; anyone else gets the space 404, so the image tells nothing about a space the account cannot see.</summary>
    public async Task<SpaceCoverContent> Handle(OpenSpaceCover query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(query.SpaceId, ct);
        await m_listing.RequireSpaceAsync(actor, query.SpaceId, ct);
        var cover = await SpaceCoverHistory.CurrentAsync(m_session, query.SpaceId, ct);
        if (cover is not { FileId: { } fileId, MimeType: { } mimeType, Sha256: { } sha256 })
        {
            throw new ApiException(404, "Prostor nemá úvodní obrázek.");
        }
        return new SpaceCoverContent(fileId, mimeType, sha256, SpaceCoverDto.VersionOf(sha256));
    }
}
