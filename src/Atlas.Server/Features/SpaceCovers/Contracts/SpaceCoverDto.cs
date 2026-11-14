using Atlas.Server.Features.SpaceCovers.Domain;

namespace Atlas.Server.Features.SpaceCovers.Contracts;

/// <summary>Wire shape of the image of a space (spaceCover.ts): <c>{ source, catalogKey, version, tinted }</c>, never null on a
/// space. Source is Auto, Catalog, Upload or None (SpaceCoverSource). CatalogKey is the chosen drawing, or for Auto the pick
/// computed on this read (null when nothing fits). Version is set for an Upload: the start of the content hash, so it changes
/// with every different upload and /api/spaces/{id}/cover?v=version can be cached for good.</summary>
public sealed record SpaceCoverDto(string Source, string? CatalogKey, string? Version, bool Tinted)
{
    /// <summary>Auto without a pick: what a space says that fits no drawing.</summary>
    public static readonly SpaceCoverDto Unmatched = new(nameof(SpaceCoverSource.Auto), null, null, true);

    public static string VersionOf(string sha256) => sha256[..16];

    public static SpaceCoverDto From(SpaceCover? cover, Space space) =>
        From(cover?.Source, cover?.CatalogKey, cover?.Sha256, cover?.Tinted, space.Name, space.Description, space.Icon);

    /// <summary>From projected columns. No row, a row that says Auto and a key the running catalog no longer knows are all the
    /// automatic pick, computed here from the name, description and icon the reader may see.</summary>
    public static SpaceCoverDto From(SpaceCoverSource? source, string? catalogKey, string? sha256, bool? tinted, string name, string description, string icon) => source switch
    {
        SpaceCoverSource.Upload when sha256 is not null => new(nameof(SpaceCoverSource.Upload), null, VersionOf(sha256), tinted ?? true),
        SpaceCoverSource.Catalog when CoverCatalog.Knows(catalogKey) => new(nameof(SpaceCoverSource.Catalog), catalogKey, null, true),
        SpaceCoverSource.None => new(nameof(SpaceCoverSource.None), null, null, true),
        _ => new(nameof(SpaceCoverSource.Auto), CoverCatalog.Match(name, description, icon), null, true)
    };
}
