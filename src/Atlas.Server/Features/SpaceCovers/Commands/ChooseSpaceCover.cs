namespace Atlas.Server.Features.SpaceCovers.Commands;

/// <param name="Source">Auto, Catalog or None by name (SpaceCoverSource); an upload has its own command.</param>
/// <param name="CatalogKey">The drawing of a Catalog choice; null for the other two.</param>
public sealed record ChooseSpaceCover(Guid SpaceId, string? Source, string? CatalogKey);
