using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.ModuleSettings;

namespace Atlas.Server.Features.AiIndex.Queries;

/// <summary><c>GET /api/admin/ai/index</c>.</summary>
public sealed record GetIndexStatus;

/// <summary>Step 2 of the verification: how many check queries (embedded titles) find their page among the five nearest pages.</summary>
public sealed record ScoreIndexVerification(Guid GenerationId, IReadOnlyList<IndexSampleQuery> Queries);

public sealed record IndexSampleQuery(Guid PageId, string Vector);

public sealed class GetIndexStatusHandler(ModuleSettingsAccess access, IndexViews views)
{
    public async Task<IndexStatus> Handle(GetIndexStatus query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        return await views.StatusAsync(ct);
    }
}

public sealed class ScoreIndexVerificationHandler(ModuleSettingsAccess access, IndexStore store)
{
    public async Task<IndexSanity> Handle(ScoreIndexVerification query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var hits = 0;
        foreach (var sample in query.Queries)
        {
            if ((await store.NearestPagesAsync(query.GenerationId, sample.Vector, ct)).Contains(sample.PageId))
            {
                hits++;
            }
        }
        return new IndexSanity(query.Queries.Count, hits);
    }
}
