using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Collaboration.Commands;

public sealed record AppendUpdate(Guid PageId, Guid Generation, Guid ClientUpdateId, Guid AuthorId, DateTimeOffset ReceivedAtUtc, byte[] Update,
    UpdateOrigin? Origin = null);
