using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceCovers.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceCovers.Commands;

public sealed class ChooseSpaceCoverHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;

    public ChooseSpaceCoverHandler(ISession session, IContentAccess access, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    /// <summary>Makes the automatic image, a catalog drawing or no image the current choice and releases an uploaded file. The
    /// same choice again is a no-op.</summary>
    public async Task<SpaceCoverChange> Handle(ChooseSpaceCover command, CancellationToken ct)
    {
        // The space rule first: whoever may not change the space gets its 404 before any validation answer.
        var (actor, space) = await SpaceCoverChanges.LockAsync(m_session, m_access, m_locks, command.SpaceId, ct);
        var source = command.Source switch
        {
            nameof(SpaceCoverSource.Auto) => SpaceCoverSource.Auto,
            nameof(SpaceCoverSource.Catalog) => SpaceCoverSource.Catalog,
            nameof(SpaceCoverSource.None) => SpaceCoverSource.None,
            // Upload is refused too: a file comes through the upload, never through a choice.
            _ => throw new ApiException(400, "Vyberte automatický obrázek, obrázek z katalogu, nebo prostor bez obrázku.")
        };
        if (source == SpaceCoverSource.Catalog && !CoverCatalog.Knows(command.CatalogKey))
        {
            throw new ApiException(400, "Tento obrázek v katalogu není. Vyberte jiný.");
        }
        if (source != SpaceCoverSource.Catalog && command.CatalogKey is not null)
        {
            throw new ApiException(400, "Klíč obrázku patří jen k volbě z katalogu.");
        }
        var released = await SpaceCoverHistory.ChooseAsync(m_session, space.Id, source, command.CatalogKey, actor.Id, ct);
        return new SpaceCoverChange(SpaceCoverDto.From(await SpaceCoverHistory.CurrentAsync(m_session, space.Id, ct), space), released);
    }
}
