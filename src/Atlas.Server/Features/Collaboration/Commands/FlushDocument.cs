using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Collaboration.Commands;

public sealed record FlushDocument(Guid PageId, Guid Generation, DocSnapshot Snapshot);
