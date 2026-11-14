namespace Atlas.Server.Features.AiIndex.Commands;

/// <summary>The live generations whose related-page lists the maintenance recomputes now (V4, plan §12).</summary>
public sealed record FindNeighborRefreshes(DateTime Now);

public sealed record NeighborRefreshesDue(IReadOnlyList<Guid> GenerationIds);

/// <summary>One batch (one short transaction) of a generation's full recompute of related pages, after page <see cref="After"/>.</summary>
public sealed record RefreshGenerationNeighbors(Guid GenerationId, Guid? After, int Batch, DateTime StartedAt);

/// <param name="Next">The page to continue after; null when the generation is done.</param>
public sealed record RefreshedGenerationNeighbors(Guid? Next);

public sealed class FindNeighborRefreshesHandler(IndexStore store)
{
    /// <summary>Daily, besides after a completed build and after activation (plan §12: "once a night").</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public async Task<NeighborRefreshesDue> Handle(FindNeighborRefreshes query, CancellationToken ct)
        => new(await store.NeighborRefreshesDueAsync(DateTime.SpecifyKind(query.Now, DateTimeKind.Utc), MaxAge, ct));
}

public sealed class RefreshGenerationNeighborsHandler(IndexStore store)
{
    public async Task<RefreshedGenerationNeighbors> Handle(RefreshGenerationNeighbors command, CancellationToken ct)
        => new(await store.RefreshGenerationNeighborsAsync(command.GenerationId, command.After, command.Batch,
            DateTime.SpecifyKind(command.StartedAt, DateTimeKind.Utc), ct));
}
