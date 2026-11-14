using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Server.Features.SpaceSettings.Contracts;
using Atlas.Server.Features.SpaceSettings.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceSettings.Queries;

internal static class CurrentSettings
{
    public static async Task<SpaceMetadataRevision> LoadAsync(ISession session, Guid spaceId, CancellationToken ct)
    {
        return await session.QueryOver<SpaceMetadataRevision>()
            .Where(x => x.SpaceId == spaceId && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct)
            ?? throw new ApiException(503, "Nastavení prostoru není dostupné.");
    }

    public static async Task<SpaceSettingsDto> ReadAsync(ISession session, Guid spaceId, CancellationToken ct)
    {
        var current = await LoadAsync(session, spaceId, ct);
        var cover = await SpaceCoverHistory.CurrentAsync(session, spaceId, ct);
        return new SpaceSettingsDto(current.SpaceId, current.Name, current.Icon, current.Color, current.Description,
            current.PinPolicy.ToString(), current.ActiveFrom,
            SpaceCoverDto.From(cover?.Source, cover?.CatalogKey, cover?.Sha256, cover?.Tinted, current.Name, current.Description, current.Icon),
            current.CommentsEnabled, current.PublicationMode.ToString());
    }
}
