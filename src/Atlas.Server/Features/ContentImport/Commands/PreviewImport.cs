using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Commands;

/// <summary>Local-only: Input is a single-pass stream owned by the endpoint for the duration of this call; it is never routed, persisted or retried by the message bus.</summary>
public sealed record PreviewImport(Stream Input, WikiImportOptions Options);
