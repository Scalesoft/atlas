namespace Atlas.Server.Features.Attachments.Contracts;

/// <summary>OpenAPI-only multipart shape; the endpoint streams and validates the actual form itself.</summary>
public sealed record AttachmentUploadForm(IFormFile File, string? Kind);
