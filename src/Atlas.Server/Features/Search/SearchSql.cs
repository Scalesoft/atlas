using NHibernate;
using Npgsql;
using NHibernate.Type;
using Atlas.Server.Features.Search.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Search;

/// <summary>Search's SQL (spec §8). Ranking, ACL filters, snippets and normalisation are the query texts of <see cref="SearchQueries"/>
/// (the atlas_search_* and atlas_similar_pages functions until Atlas 3.6); this class binds arguments and reads columns.</summary>
public sealed class SearchSql
{
    private const string HitColumns = "page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias";
    /// <summary>After the nine hit columns of a hybrid search, before widgets.</summary>
    private static readonly (string Name, IType Type)[] HybridScalars =
    [
        ("is_draft", NHibernateUtil.Boolean), ("semantic", NHibernateUtil.Boolean), ("passage", NHibernateUtil.String),
        ("passage_heading_title", NHibernateUtil.String), ("passage_heading_id", NHibernateUtil.String),
    ];
    private static readonly string PublishedFilteredText = SearchQueries.PublishedFiltered();
    private static readonly string DraftsText = SearchQueries.Drafts();
    private readonly ISession m_session;

    public SearchSql(ISession session)
    {
        m_session = session;
    }

    /// <param name="hybrid">S1/S2: the search reads the knowledge index too. Its HNSW scan then continues past
    /// <c>ef_search</c> until enough chunks pass the access filters (pgvector 0.8 iterative scan, bounded by
    /// <c>max_scan_tuples</c>), so hidden pages never starve the visible ones (plan §11, R4).</param>
    public async Task PrepareAsync(CancellationToken ct, bool hybrid = false)
    {
        RequireTransaction();
        // Keep plans specific to the query/scope. Latency optimization: disable JIT only for this search transaction.
        // Profiling showed ~867 ms of JIT compilation in a ~1 s query when missing ACL statistics inflated plan costs.
        // The setting covers the whole composed query text.
        // pg_trgm.similarity_threshold is the did-you-mean threshold (S3, SearchQueries.DidYouMean); search ranks with <%.
        await m_session.CreateSQLQuery(PlannerSettings.Select(PlannerSettings.Base(PlannerSettings.SearchTimeout),
            PlannerSettings.WordSimilarity(PlannerSettings.SearchSimilarity), PlannerSettings.Similarity(PlannerSettings.SearchSimilarity)))
            .UniqueResultAsync(ct);
        if (hybrid)
        {
            await m_session.CreateSQLQuery(PlannerSettings.Select(PlannerSettings.Vectors)).UniqueResultAsync(ct);
        }
    }

    /// <summary>
    /// <paramref name="statement"/> under its own statement timeout of <paramref name="milliseconds"/>, in a savepoint: one that
    /// exceeds it is <paramref name="onTimeout"/> and leaves the transaction usable; either way the transaction's timeout is back after.
    /// </summary>
    public async Task<T> BoundedAsync<T>(Func<Task<T>> statement, int milliseconds, T onTimeout, CancellationToken ct)
    {
        RequireTransaction();
        await m_session.CreateSQLQuery("SAVEPOINT atlas_bounded").ExecuteUpdateAsync(ct);
        var previous = await m_session.CreateSQLQuery("SELECT current_setting('statement_timeout') AS previous, set_config('statement_timeout',:bound,true) AS bound")
            .AddScalar("previous", NHibernateUtil.String).SetParameter("bound", milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms")
            .UniqueResultAsync<string>(ct);
        try
        {
            var result = await statement();
            await m_session.CreateSQLQuery("RELEASE SAVEPOINT atlas_bounded").ExecuteUpdateAsync(ct);
            await m_session.CreateSQLQuery("SELECT set_config('statement_timeout',:previous,true)").SetParameter("previous", previous).UniqueResultAsync(ct);
            return result;
        }
        catch (Exception exception) when (IsStatementTimeout(exception) && !ct.IsCancellationRequested)
        {
            // Rolling back to the savepoint also restores the timeout set after it.
            await m_session.CreateSQLQuery("ROLLBACK TO SAVEPOINT atlas_bounded").ExecuteUpdateAsync(ct);
            await m_session.CreateSQLQuery("RELEASE SAVEPOINT atlas_bounded").ExecuteUpdateAsync(ct);
            return onTimeout;
        }
    }

    /// <summary>Whether the generation a query was embedded for is still the active one. The hybrid SQL checks it again in
    /// its own snapshot; this read only decides whether the response may say the vector part ran.</summary>
    public async Task<bool> GenerationActiveAsync(Guid generationId, CancellationToken ct)
    {
        return await m_session.CreateSQLQuery("""SELECT EXISTS (SELECT 1 FROM "IndexGeneration" WHERE "Id"=:id AND "State"='Active') AS active""")
            .AddScalar("active", NHibernateUtil.Boolean)
            .SetParameter("id", generationId, NHibernateUtil.Guid)
            .UniqueResultAsync<bool>(ct);
    }

    /// <summary>S1/S2 in the published scope (also the Personal API, O11). Columns: the nine hit columns, is_draft (always
    /// false), semantic, passage, passage_heading_title, passage_heading_id, widgets.</summary>
    public Task<IList<object[]>> HybridPublishedAsync(SearchQueryParameters parameters, int take, int skip,
        DateTime? changedSince, bool latest, SemanticQuery semantic, CancellationToken ct)
    {
        var query = Hits("SELECT " + HitColumns + ",false AS is_draft,semantic,passage,passage_heading_title,passage_heading_id"
            + " FROM (" + SearchQueries.HybridPublished + ") moved",
            "hits.is_draft", HybridScalars);
        return Semantic(Window(Bind(query, parameters), take, skip, changedSince, latest), semantic).ListAsync<object[]>(ct);
    }

    /// <summary>S1/S2 in the unified scope: the hybrid published scope composed with today's drafts. Columns as
    /// <see cref="HybridPublishedAsync"/>, is_draft per row.</summary>
    public Task<IList<object[]>> HybridAllAsync(SearchQueryParameters parameters, int take, int skip,
        DateTime? changedSince, bool latest, SemanticQuery semantic, CancellationToken ct)
    {
        var query = Hits("SELECT " + HitColumns + ",is_draft,semantic,passage,passage_heading_title,passage_heading_id"
            + " FROM (" + SearchQueries.HybridAll + ") moved",
            "hits.is_draft", HybridScalars);
        return Semantic(Window(Bind(query, parameters), take, skip, changedSince, latest), semantic).ListAsync<object[]>(ct);
    }

    /// <summary>S3: at most one readable published title or alias close to the query; no model (O5).</summary>
    public Task<string?> DidYouMeanAsync(SearchQueryParameters parameters, CancellationToken ct)
    {
        return m_session.CreateSQLQuery("SELECT suggestion FROM (" + SearchQueries.DidYouMean + ") moved")
            .AddScalar("suggestion", NHibernateUtil.String)
            .SetParameter("query", parameters.Query, NHibernateUtil.String)
            .SetParameter("actor", parameters.Actor, NHibernateUtil.Guid)
            .SetParameter("stamp", parameters.Stamp, NHibernateUtil.String)
            .SetParameter("space", parameters.Space, NHibernateUtil.Guid)
            .UniqueResultAsync<string?>(ct);
    }

    /// <summary>P1 runs while a title is typed and must never hold up creating the page: a short statement_timeout (the
    /// handler answers an empty list on 57014) and the trigram threshold of the title pre-selection (SearchQueries.Similar).</summary>
    public async Task PrepareSimilarAsync(CancellationToken ct, bool semantic = false)
    {
        RequireTransaction();
        await m_session.CreateSQLQuery(PlannerSettings.Select(PlannerSettings.Base(PlannerSettings.TypingTimeout),
            PlannerSettings.Similarity(PlannerSettings.TypingSimilarity))).UniqueResultAsync(ct);
        if (semantic)
        {
            // The vector part reads the HNSW indexes of KnowledgePage with the iterative scan of hybrid search (V3).
            await m_session.CreateSQLQuery(PlannerSettings.Select(PlannerSettings.Vectors)).UniqueResultAsync(ct);
        }
    }

    public Task<IList<object[]>> SimilarAsync(string normalized, string words, Guid actor, string stamp, Guid? space,
        Guid? exclude, int take, CancellationToken ct)
    {
        return m_session.CreateSQLQuery("SELECT page_id,space_id,space_name,title,is_draft,matched_by,heading_title,heading_id,score FROM (" + SearchQueries.Similar + ") moved")
            .AddScalar("page_id", NHibernateUtil.Guid)
            .AddScalar("space_id", NHibernateUtil.Guid)
            .AddScalar("space_name", NHibernateUtil.String)
            .AddScalar("title", NHibernateUtil.String)
            .AddScalar("is_draft", NHibernateUtil.Boolean)
            .AddScalar("matched_by", NHibernateUtil.String)
            .AddScalar("heading_title", NHibernateUtil.String)
            .AddScalar("heading_id", NHibernateUtil.String)
            .AddScalar("score", NHibernateUtil.Single)
            .SetParameter("query", normalized, NHibernateUtil.String)
            .SetParameter("words", words, NHibernateUtil.String)
            .SetParameter("actor", actor, NHibernateUtil.Guid)
            .SetParameter("stamp", stamp, NHibernateUtil.String)
            .SetParameter("space", space, NHibernateUtil.Guid)
            .SetParameter("exclude", exclude, NHibernateUtil.Guid)
            .SetParameter("take", take)
            .ListAsync<object[]>(ct);
    }

    /// <summary>P1, vector part (V5, <see cref="SearchQueries.SimilarSemantic"/>): readable published pages whose title or content is near the title's
    /// vector, at 0.75 or more. Columns: page_id, space_id, space_name, title, score. The caller has run
    /// <see cref="PrepareSimilarAsync"/> with <c>semantic</c>.</summary>
    public Task<IList<object[]>> SimilarSemanticAsync(SemanticQuery semantic, Guid actor, string stamp, Guid? space, Guid? exclude, int take,
        CancellationToken ct)
    {
        return m_session.CreateSQLQuery("SELECT page_id,space_id,space_name,title,score FROM (" + SearchQueries.SimilarSemantic + ") moved")
            .AddScalar("page_id", NHibernateUtil.Guid)
            .AddScalar("space_id", NHibernateUtil.Guid)
            .AddScalar("space_name", NHibernateUtil.String)
            .AddScalar("title", NHibernateUtil.String)
            .AddScalar("score", NHibernateUtil.Single)
            .SetParameter("generation", semantic.GenerationId, NHibernateUtil.Guid)
            .SetParameter("vector", QueryVectorText.Format(semantic.Vector), NHibernateUtil.StringClob)
            .SetParameter("actor", actor, NHibernateUtil.Guid)
            .SetParameter("stamp", stamp, NHibernateUtil.String)
            .SetParameter("space", space, NHibernateUtil.Guid)
            .SetParameter("exclude", exclude, NHibernateUtil.Guid)
            .SetParameter("take", take)
            .SetParameter("min_similarity", 0.75f, NHibernateUtil.Single)
            .ListAsync<object[]>(ct);
    }

    public Task<IList<object[]>> PublishedAsync(SearchQueryParameters parameters, int take, int skip, CancellationToken ct)
    {
        var query = Hits("SELECT " + HitColumns + " FROM (" + SearchQueries.Published + ") moved");
        return Bind(query, parameters).SetParameter("take", take).SetParameter("skip", skip).ListAsync<object[]>(ct);
    }

    public Task<IList<object[]>> PublishedFilteredAsync(SearchQueryParameters parameters, int take, int skip,
        DateTime? changedSince, bool latest, CancellationToken ct)
    {
        var query = Hits("SELECT " + HitColumns + " FROM (" + PublishedFilteredText + ") moved");
        return Window(Bind(query, parameters), take, skip, changedSince, latest).ListAsync<object[]>(ct);
    }

    /// <summary>The unified scope reports draftness per row, so this is the one call with a tenth column.</summary>
    public Task<IList<object[]>> AllAsync(SearchQueryParameters parameters, int take, int skip,
        DateTime? changedSince, bool latest, CancellationToken ct)
    {
        var query = Hits("SELECT " + HitColumns + ",is_draft FROM (" + SearchQueries.All + ") moved", "hits.is_draft", [("is_draft", NHibernateUtil.Boolean)]);
        return Window(Bind(query, parameters), take, skip, changedSince, latest).ListAsync<object[]>(ct);
    }

    public Task<IList<object[]>> DraftsAsync(SearchQueryParameters parameters, int take, int skip,
        DateTime? changedSince, bool latest, CancellationToken ct)
    {
        var query = Hits("SELECT " + HitColumns + " FROM (" + DraftsText + ") moved", "true");
        return Window(Bind(query, parameters), take, skip, changedSince, latest).ListAsync<object[]>(ct);
    }

    public Task<IList<object[]>> SpacesAsync(SearchQueryParameters parameters, CancellationToken ct)
    {
        var query = m_session.CreateSQLQuery("SELECT space_id,space_name,space_icon,space_color FROM (" + SearchQueries.Spaces + ") moved")
            .AddScalar("space_id", NHibernateUtil.Guid)
            .AddScalar("space_name", NHibernateUtil.String)
            .AddScalar("space_icon", NHibernateUtil.String)
            .AddScalar("space_color", NHibernateUtil.String);
        return Bind(query, parameters).ListAsync<object[]>(ct);
    }

    /// <summary>The text a hit's snippet is cut from when its headline cannot show the hit (review U3-2): the published search body,
    /// or the working revision's Markdown for a draft hit. Only pages the search already returned to the actor.</summary>
    // shortcut: the whole text of each such page (a requirement page, ≤ 2 MB); cut it in SQL if pages that large become common.
    public async Task<IReadOnlyDictionary<(Guid PageId, bool IsDraft), string>> PageTextsAsync(IReadOnlyCollection<(Guid PageId, bool IsDraft)> pages,
        CancellationToken ct)
    {
        Guid[] Of(bool draft) => pages.Where(x => x.IsDraft == draft).Select(x => x.PageId).DefaultIfEmpty(Guid.Empty).ToArray();
        var rows = await m_session.CreateSQLQuery("""
            SELECT d."PageId" AS id, false AS draft, d."Body" AS text FROM "SearchDocument" d WHERE d."PageId" IN (:published)
            UNION ALL
            SELECT r."PageId", true, r."Markdown" FROM "PageRevision" r WHERE r."PageId" IN (:drafts) AND r."ActiveTo" IS NULL
            """)
            .AddScalar("id", NHibernateUtil.Guid).AddScalar("draft", NHibernateUtil.Boolean).AddScalar("text", NHibernateUtil.String)
            .SetParameterList("published", Of(false)).SetParameterList("drafts", Of(true))
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => ((Guid)row[0], (bool)row[1]), row => (string)row[2]);
    }

    /// <summary>The spaces among <paramref name="spaceIds"/> switched out of AI and MCP (G5), in the search transaction's snapshot.</summary>
    public async Task<IReadOnlySet<Guid>> ExcludedFromAiAsync(IReadOnlyCollection<Guid> spaceIds, CancellationToken ct)
    {
        if (spaceIds.Count == 0)
        {
            return new HashSet<Guid>();
        }
        var rows = await m_session.CreateSQLQuery($$"""SELECT s."Id" AS id FROM "Space" s WHERE s."Id" IN (:ids) AND NOT {{AccessSql.SpaceInAi("s.\"Id\"")}}""")
            .AddScalar("id", NHibernateUtil.Guid)
            .SetParameterList("ids", spaceIds.Distinct().ToArray())
            .ListAsync<Guid>(ct);
        return rows.ToHashSet();
    }

    private ISQLQuery Hits(string sql, string isDraft = "false", (string Name, IType Type)[]? extra = null)
    {
        // Explicit columns, not SELECT *: AddScalar reads by name and this keeps name and object[] index together.
        // Headings only place a contact or issue (SearchWidgets.Read), so a page without either skips parsing its whole document
        // (SE3-1: ~1 ms per hit, 50 ms per query on the real wiki).
        var query = m_session.CreateSQLQuery($"""
            WITH hits AS MATERIALIZED (SELECT ordered.*, row_number() OVER () AS result_order FROM ({sql}) ordered)
            SELECT hits.*, CASE WHEN revision."ContentJson" LIKE '%"contact%' OR revision."ContentJson" LIKE '%"partyContactId"%' OR revision."ContentJson" LIKE '%"youtrackIssue"%'
              THEN CAST(jsonb_path_query_array(CAST(revision."ContentJson" AS jsonb),
              'strict $.document.** ? (@.type == "contactCard" || @.type == "contactInline" || @.type == "youtrackIssue" || @.type == "heading")', jsonb_build_object(), true) AS text)
              ELSE '[]' END AS widgets
            FROM hits
            JOIN "Page" page ON page."Id" = hits.page_id
            JOIN "PageRevision" revision ON revision."PageId" = page."Id"
              AND (({isDraft} AND revision."ActiveTo" IS NULL)
                OR (NOT ({isDraft}) AND revision."Id" = page."PublishedRevisionId"))
            ORDER BY hits.result_order
            """)
            .AddScalar("page_id", NHibernateUtil.Guid)
            .AddScalar("space_id", NHibernateUtil.Guid)
            .AddScalar("space_name", NHibernateUtil.String)
            .AddScalar("title", NHibernateUtil.String)
            .AddScalar("snippet", NHibernateUtil.String)
            .AddScalar("changed_at", NHibernateUtil.UtcDateTime)
            .AddScalar("heading_title", NHibernateUtil.String)
            .AddScalar("heading_id", NHibernateUtil.String)
            .AddScalar("matched_alias", NHibernateUtil.String);
        foreach (var (name, type) in extra ?? [])
        {
            query.AddScalar(name, type);
        }
        return query.AddScalar("widgets", NHibernateUtil.String);
    }

    private static IQuery Bind(ISQLQuery query, SearchQueryParameters parameters)
    {
        return query
            .SetParameter("query", parameters.Query, NHibernateUtil.String)
            .SetParameter("prefix", parameters.Prefix, NHibernateUtil.String)
            .SetParameter("fts", parameters.Fts, NHibernateUtil.String)
            .SetParameter("phrase", parameters.Phrase, NHibernateUtil.Boolean)
            .SetParameter("fuzzy", parameters.Fuzzy, NHibernateUtil.Boolean)
            .SetParameter("actor", parameters.Actor, NHibernateUtil.Guid)
            .SetParameter("stamp", parameters.Stamp, NHibernateUtil.String)
            .SetParameter("space", parameters.Space, NHibernateUtil.Guid);
    }

    private static IQuery Window(IQuery query, int take, int skip, DateTime? changedSince, bool latest)
    {
        return query
            .SetParameter("take", take)
            .SetParameter("skip", skip)
            .SetParameter("changed_since", changedSince, NHibernateUtil.UtcDateTime)
            .SetParameter("latest", latest);
    }

    private static IQuery Semantic(IQuery query, SemanticQuery semantic)
    {
        return query
            .SetParameter("generation", semantic.GenerationId, NHibernateUtil.Guid)
            .SetParameter("embedding", QueryVectorText.Format(semantic.Vector), NHibernateUtil.StringClob)
            // The default of the hybrid functions until Atlas 3.6: the least cosine similarity of a vector hit.
            .SetParameter("min_similarity", 0.45f, NHibernateUtil.Single);
    }

    public static bool IsStatementTimeout(Exception exception)
    {
        // NHibernate wraps the driver error in GenericADOException; the SQLSTATE lives on the inner PostgresException.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "57014" })
            {
                return true;
            }
        }
        return false;
    }

    private void RequireTransaction()
    {
        // set_config(name, value, true) is SET LOCAL: without a transaction it would last one statement and the
        // search would silently run with the server defaults, so require one the way AtlasLocks does.
        if (m_session.GetCurrentTransaction() is not { IsActive: true })
        {
            throw new InvalidOperationException("Search settings require an active transaction.");
        }
    }
}
