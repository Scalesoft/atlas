using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.Search.Queries;

/// <summary>P1: suggestions while a title is typed. Access is decided in SearchQueries.Similar exactly as in search
/// (actor locked here, ACL in SQL); the lookup never holds up creating the page, so a timeout answers an empty list.</summary>
public sealed class FindSimilarPagesHandler
{
    public const string MatchedByMeaning = "meaning";
    private static readonly SimilarPagesResponse Nothing = new([]);
    private readonly IContentAccess m_access;
    private readonly SearchSql m_sql;
    private readonly Atlas.Server.Features.Ai.Access.AiPolicyResolver m_policy;

    public FindSimilarPagesHandler(IContentAccess access, SearchSql sql, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy)
    {
        m_access = access;
        m_sql = sql;
        m_policy = policy;
    }

    public async Task<SimilarPagesResponse> Handle(FindSimilarPages query, CancellationToken ct)
    {
        // Same order as SearchHandler: a revoked credential answers 401 before a bad argument answers 400, and a lock
        // wait past statement_timeout is caught like a slow query.
        try
        {
            await m_sql.PrepareSimilarAsync(ct, query.Semantic is not null);
            var actor = await m_access.LockActorAsync(null, ct);
            if (query.Limit is < 1 or > 5)
            {
                throw new ApiException(400, "Počet podobných stránek musí být 1–5.");
            }
            string normalized;
            try
            {
                normalized = SearchText.Normalize(query.Title);
            }
            catch (ArgumentException exception)
            {
                throw new ApiException(400, exception.Message);
            }
            if (normalized.Length < 3)
            {
                return Nothing;
            }
            var rows = await m_sql.SimilarAsync(normalized, SearchText.AnyWordQuery(normalized), actor.Id, actor.SecurityStamp,
                query.SpaceId, query.ExcludePageId, query.Limit, ct);
            var items = rows.Select(row => new SimilarPage((Guid)row[0], (Guid)row[1], (string)row[2],
                (string)row[3], (bool)row[4], (string)row[5], row[6] as string, row[7] as string, Convert.ToSingle(row[8]))).ToList();
            if (query.Semantic is { } semantic && items.Count < query.Limit && (await m_policy.ResolveAsync(actor.Id, ct)).Enabled)
            {
                // P1 phase 2: pages similar in meaning follow the lexical ones and fill the list up to its limit; published only.
                var meaning = await m_sql.SimilarSemanticAsync(semantic, actor.Id, actor.SecurityStamp, query.SpaceId, query.ExcludePageId,
                    query.Limit + items.Count, ct);
                var known = items.Select(item => item.Id).ToHashSet();
                items.AddRange(meaning.Where(row => !known.Contains((Guid)row[0])).Take(query.Limit - items.Count)
                    .Select(row => new SimilarPage((Guid)row[0], (Guid)row[1], (string)row[2], (string)row[3], false, MatchedByMeaning, null, null,
                        Convert.ToSingle(row[4]))));
            }
            return new SimilarPagesResponse(items);
        }
        catch (Exception exception) when (SearchSql.IsStatementTimeout(exception) && !ct.IsCancellationRequested)
        {
            return Nothing;
        }
    }
}
