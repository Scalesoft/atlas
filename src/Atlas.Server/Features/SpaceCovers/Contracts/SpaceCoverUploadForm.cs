namespace Atlas.Server.Features.SpaceCovers.Contracts;

/// <summary>OpenAPI-only multipart shape; the endpoint reads and validates the actual form itself.</summary>
public sealed record SpaceCoverUploadForm(IFormFile File);
