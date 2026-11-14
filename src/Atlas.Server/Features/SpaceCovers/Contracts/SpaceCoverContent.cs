namespace Atlas.Server.Features.SpaceCovers.Contracts;

/// <summary>Metadata needed to serve the image. The endpoint opens the file; a handler must not hand an open stream across a transaction boundary.</summary>
public sealed record SpaceCoverContent(Guid FileId, string MimeType, string Sha256, string Version);
