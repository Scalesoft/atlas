using NHibernate;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AiSuggestions;

/// <summary>
/// The only C# file with the SQL of E2, E4, P2 and P3 (P1's vector part is <see cref="SearchSql.SimilarSemanticAsync"/>).
/// Access, publication, trash, templates, generation and space inclusion are decided in the query text of
/// <see cref="SemanticSuggestionQueries"/>; this class binds arguments and reads columns. Vectors travel as <see cref="QueryVectorText"/>.
/// </summary>
public sealed class SemanticSuggestionSql(ISession session)
{
    /// <summary>A suggestion must never hold up writing: a short statement_timeout (the callers answer without the part that
    /// ran too long), and for a read of the HNSW indexes the iterative scan of hybrid search (filters on every scanned row).</summary>
    public async Task PrepareAsync(bool vectors, CancellationToken ct)
    {
        if (session.GetCurrentTransaction() is not { IsActive: true })
        {
            throw new InvalidOperationException("Suggestion settings require an active transaction.");
        }
        await session.CreateSQLQuery(PlannerSettings.Select(PlannerSettings.Base(PlannerSettings.TypingTimeout))).UniqueResultAsync(ct);
        if (vectors)
        {
            await session.CreateSQLQuery(PlannerSettings.Select(PlannerSettings.Vectors)).UniqueResultAsync(ct);
        }
    }

    public Task<bool> SpaceInAiAsync(Guid spaceId, CancellationToken ct)
        => session.CreateSQLQuery($"SELECT {AccessSql.SpaceInAi(":space")} AS included")
            .AddScalar("included", NHibernateUtil.Boolean)
            .SetParameter("space", spaceId, NHibernateUtil.Guid)
            .UniqueResultAsync<bool>(ct);

    public Task<bool> MayCreateBelowPageAsync(Guid spaceId, UserAccount actor, CancellationToken ct)
        => session.CreateSQLQuery(AccessSql.MayCreateBelowPage)
            .AddScalar("allowed", NHibernateUtil.Boolean)
            .SetParameter("space", spaceId, NHibernateUtil.Guid)
            .SetParameter("actor", actor.Id, NHibernateUtil.Guid)
            .SetParameter("stamp", actor.SecurityStamp, NHibernateUtil.String)
            .UniqueResultAsync<bool>(ct);

    /// <summary>Columns: phrase (normalized), page_id, space_id, space_name, title, matched_by.</summary>
    public Task<IList<object[]>> LinkCandidatesAsync(IReadOnlyList<string> normalizedPhrases, UserAccount actor, Guid exclude, Guid space, CancellationToken ct)
        => session.CreateSQLQuery($"SELECT phrase,page_id,space_id,space_name,title,matched_by FROM ({SemanticSuggestionQueries.LinkCandidates}) candidates")
            .AddScalar("phrase", NHibernateUtil.String).AddScalar("page_id", NHibernateUtil.Guid).AddScalar("space_id", NHibernateUtil.Guid)
            .AddScalar("space_name", NHibernateUtil.String).AddScalar("title", NHibernateUtil.String).AddScalar("matched_by", NHibernateUtil.String)
            .SetParameter("phrases", TextArray(normalizedPhrases), NHibernateUtil.StringClob)
            .SetParameter("actor", actor.Id, NHibernateUtil.Guid)
            .SetParameter("stamp", actor.SecurityStamp, NHibernateUtil.String)
            .SetParameter("exclude", exclude, NHibernateUtil.Guid)
            .SetParameter("space", space, NHibernateUtil.Guid)
            .ListAsync<object[]>(ct);

    /// <summary>Columns: phrase (as given), page_id, space_id, space_name, title, similarity.</summary>
    public Task<IList<object[]>> LinkCandidatesSemanticAsync(Guid generation, IReadOnlyList<string> phrases, IReadOnlyList<float[]> vectors,
        UserAccount actor, Guid exclude, CancellationToken ct)
        => session.CreateSQLQuery($"SELECT phrase,page_id,space_id,space_name,title,similarity FROM ({SemanticSuggestionQueries.LinkCandidatesSemantic}) candidates")
            .AddScalar("phrase", NHibernateUtil.String).AddScalar("page_id", NHibernateUtil.Guid).AddScalar("space_id", NHibernateUtil.Guid)
            .AddScalar("space_name", NHibernateUtil.String).AddScalar("title", NHibernateUtil.String).AddScalar("similarity", NHibernateUtil.Single)
            .SetParameter("generation", generation, NHibernateUtil.Guid)
            .SetParameter("phrases", TextArray(phrases), NHibernateUtil.StringClob)
            .SetParameter("vectors", TextArray(vectors.Select(x => QueryVectorText.Format(x)).ToArray()), NHibernateUtil.StringClob)
            .SetParameter("actor", actor.Id, NHibernateUtil.Guid)
            .SetParameter("stamp", actor.SecurityStamp, NHibernateUtil.String)
            .SetParameter("exclude", exclude, NHibernateUtil.Guid)
            .SetParameter("min_similarity", 0.80f, NHibernateUtil.Single)
            .ListAsync<object[]>(ct);

    /// <summary>Columns: page_id, similarity; most similar first.</summary>
    public Task<IList<object[]>> RankByContextAsync(Guid generation, float[] vector, IReadOnlyList<Guid> candidates, UserAccount actor, CancellationToken ct)
        => session.CreateSQLQuery($"SELECT page_id,similarity FROM ({SemanticSuggestionQueries.RankByContext}) ranked")
            .AddScalar("page_id", NHibernateUtil.Guid).AddScalar("similarity", NHibernateUtil.Single)
            .SetParameter("generation", generation, NHibernateUtil.Guid)
            .SetParameter("vector", QueryVectorText.Format(vector), NHibernateUtil.StringClob)
            .SetParameter("candidates", "{" + string.Join(',', candidates) + "}", NHibernateUtil.String)
            .SetParameter("actor", actor.Id, NHibernateUtil.Guid)
            .SetParameter("stamp", actor.SecurityStamp, NHibernateUtil.String)
            .ListAsync<object[]>(ct);

    /// <summary>Zero rows or one: parent_id (null = the root of the space), path_ids and path_titles as JSON arrays (top down,
    /// the parent last).</summary>
    public Task<IList<object[]>> PlacementAsync(Guid generation, float[] vector, Guid space, UserAccount actor, bool rootAllowed, CancellationToken ct)
        => session.CreateSQLQuery($"""
                SELECT parent_id,CAST(to_jsonb(path_ids) AS text) AS path_ids,CAST(to_jsonb(path_titles) AS text) AS path_titles
                FROM ({SemanticSuggestionQueries.Placement}) placement
                """)
            .AddScalar("parent_id", NHibernateUtil.Guid).AddScalar("path_ids", NHibernateUtil.String)
            .AddScalar("path_titles", NHibernateUtil.String)
            .SetParameter("generation", generation, NHibernateUtil.Guid)
            .SetParameter("vector", QueryVectorText.Format(vector), NHibernateUtil.StringClob)
            .SetParameter("space", space, NHibernateUtil.Guid)
            .SetParameter("actor", actor.Id, NHibernateUtil.Guid)
            .SetParameter("stamp", actor.SecurityStamp, NHibernateUtil.String)
            .SetParameter("root_allowed", rootAllowed, NHibernateUtil.Boolean)
            .SetParameter("min_similarity", 0.45f, NHibernateUtil.Single)
            .ListAsync<object[]>(ct);

    /// <summary>Zero rows or one: template_id, title, matched_by. Without <paramref name="semantic"/> the dictionary only.</summary>
    public Task<IList<object[]>> TemplateAsync(SemanticQuery? semantic, string normalizedTitle, Guid space, UserAccount actor, CancellationToken ct)
        => session.CreateSQLQuery($"SELECT template_id,title,matched_by FROM ({SemanticSuggestionQueries.Template}) suggestion")
            .AddScalar("template_id", NHibernateUtil.Guid).AddScalar("title", NHibernateUtil.String).AddScalar("matched_by", NHibernateUtil.String)
            .SetParameter("generation", semantic?.GenerationId, NHibernateUtil.Guid)
            .SetParameter("vector", semantic is null ? null : QueryVectorText.Format(semantic.Vector), NHibernateUtil.StringClob)
            .SetParameter("title", normalizedTitle, NHibernateUtil.String)
            .SetParameter("space", space, NHibernateUtil.Guid)
            .SetParameter("actor", actor.Id, NHibernateUtil.Guid)
            .SetParameter("stamp", actor.SecurityStamp, NHibernateUtil.String)
            .SetParameter("min_similarity", 0.75f, NHibernateUtil.Single)
            .ListAsync<object[]>(ct);

    /// <summary>A PostgreSQL array literal of text: every element quoted, quotes and backslashes escaped.</summary>
    internal static string TextArray(IEnumerable<string> values)
        => "{" + string.Join(',', values.Select(value => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "}";

    /// <summary>The JSON array text the placement SQL returns for path_ids and path_titles.</summary>
    internal static T[] JsonArray<T>(string? text) => string.IsNullOrEmpty(text) ? [] : System.Text.Json.JsonSerializer.Deserialize<T[]>(text) ?? [];
}
