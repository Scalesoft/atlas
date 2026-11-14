namespace Atlas.Server.Features.SpaceCovers.Contracts;

/// <param name="CatalogKey">The drawing the automatic image would be; null when nothing fits.</param>
public sealed record SpaceCoverMatch(string? CatalogKey);
