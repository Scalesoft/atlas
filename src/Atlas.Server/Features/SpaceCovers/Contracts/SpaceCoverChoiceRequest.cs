namespace Atlas.Server.Features.SpaceCovers.Contracts;

/// <param name="Source">Auto, Catalog or None (SpaceCoverSource by name).</param>
/// <param name="CatalogKey">The chosen drawing; only with Catalog.</param>
public sealed record SpaceCoverChoiceRequest(string? Source, string? CatalogKey);
