using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;

namespace Atlas.Server.Features.PageLinks.Queries;

// Reads live in a …Queries namespace, so their transactions commit without waiting for the WAL flush (TransactionIntent).
/// <param name="Limit">Of links here; related pages are at most <see cref="GetPageConnectionsHandler.RelatedLimit"/>.</param>
public sealed record GetPageConnections(Guid PageId, int Limit)
{
    /// <summary>Whether to read related pages: the <c>page.related</c> capability, decided before the handler from memory
    /// (A3), so the read transaction never loads AI configuration.</summary>
    public bool Related { get; init; }

    /// <summary>The query the endpoint sends: the capability as it is now.</summary>
    public static async ValueTask<GetPageConnections> ForReaderAsync(Guid pageId, int limit, AiCapabilities capabilities, CancellationToken ct)
        => new(pageId, limit) { Related = await capabilities.IsAvailableAsync(AiTaskCatalog.PageRelated, ct) };
}
