using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Collaboration.Commands;

public sealed record BindDocumentSchema(Guid PageId, Guid Generation, MarkdownDocumentSchema? Schema);
