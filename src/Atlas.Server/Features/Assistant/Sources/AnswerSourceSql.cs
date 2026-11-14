using NHibernate;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Identity;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Assistant.Sources;

/// <summary>A row of <see cref="AnswerSourceQueries.Sources"/>.</summary>
public sealed record AnswerSourceRow(int Ordinal, Guid GenerationId, Guid PageId, Guid RevisionId, Guid SpaceId, string SpaceName,
    string Title, string? HeadingId, string? HeadingTitle, string Text, int Rank);

/// <summary>A row of <see cref="AnswerSourceQueries.Status"/>.</summary>
public sealed record AnswerSourceStatusRow(Guid PageId, Guid RevisionId, bool Available, bool Changed);

/// <summary>The current page and its chunk text, for <see cref="PageSourceAdapter.LoadTextAsync"/>.</summary>
public sealed record AnswerChunkText(Guid GenerationId, Guid PageId, Guid RevisionId, int Ordinal, string Text);

/// <summary>A row of <see cref="AnswerSourceSql.DescribeAsync"/>.</summary>
public sealed record AnswerChunkDescription(Guid GenerationId, Guid PageId, Guid RevisionId, int Ordinal, string Title, Guid SpaceId,
    string SpaceName, string? HeadingTitle);

/// <summary>The SQL calls of the page source (migration 2026100740): it binds the arguments of <see cref="AnswerSourceQueries"/> and
/// reads columns.</summary>
public sealed class AnswerSourceSql(ISession session)
{
    /// <summary>
    /// The sources of one question. Runs with the settings of the hybrid search (SearchSql.PrepareAsync: a 2 s statement timeout,
    /// no JIT, custom plans, the trigram threshold of the title tier and the widened HNSW scan) and puts the timeout, JIT and plan
    /// mode of the caller's transaction back afterwards: the preparation transaction goes on writing the turn. A timeout aborts
    /// the transaction and answers 503 like search.
    /// </summary>
    public async Task<IReadOnlyList<AnswerSourceRow>> SourcesAsync(Guid actor, string stamp, Guid? space, Guid? page, AnswerQueryText text,
        SemanticQuery? vector, int pages, int take, int perPage, float minSimilarity, CancellationToken ct)
    {
        RequireTransaction();
        var previous = await session.CreateSQLQuery("""
            SELECT current_setting('statement_timeout') AS timeout, current_setting('jit') AS jit, current_setting('plan_cache_mode') AS plans
            """)
            .AddScalar("timeout", NHibernateUtil.String).AddScalar("jit", NHibernateUtil.String).AddScalar("plans", NHibernateUtil.String)
            .UniqueResultAsync<object[]>(ct);
        await session.CreateSQLQuery(PlannerSettings.Select(PlannerSettings.Base(PlannerSettings.SearchTimeout),
            PlannerSettings.WordSimilarity(PlannerSettings.SearchSimilarity), PlannerSettings.Vectors)).UniqueResultAsync(ct);
        IList<object[]> rows;
        try
        {
            rows = await session.CreateSQLQuery($"""
                SELECT chunk_ordinal,generation_id,page_id,revision_id,space_id,space_name,title,heading_id,heading_title,text,rank
                FROM ({AnswerSourceQueries.Sources}) sources
                """)
                .AddScalar("chunk_ordinal", NHibernateUtil.Int16)
                .AddScalar("generation_id", NHibernateUtil.Guid)
                .AddScalar("page_id", NHibernateUtil.Guid)
                .AddScalar("revision_id", NHibernateUtil.Guid)
                .AddScalar("space_id", NHibernateUtil.Guid)
                .AddScalar("space_name", NHibernateUtil.String)
                .AddScalar("title", NHibernateUtil.String)
                .AddScalar("heading_id", NHibernateUtil.String)
                .AddScalar("heading_title", NHibernateUtil.String)
                .AddScalar("text", NHibernateUtil.String)
                .AddScalar("rank", NHibernateUtil.Int32)
                .SetParameter("actor", actor, NHibernateUtil.Guid)
                .SetParameter("stamp", stamp, NHibernateUtil.String)
                .SetParameter("space", space, NHibernateUtil.Guid)
                .SetParameter("page", page, NHibernateUtil.Guid)
                .SetParameter("query", text.Query, NHibernateUtil.String)
                .SetParameter("prefix", text.Prefix, NHibernateUtil.String)
                .SetParameter("fts", text.Fts, NHibernateUtil.String)
                .SetParameter("phrase", false, NHibernateUtil.Boolean)
                .SetParameter("generation", vector?.GenerationId, NHibernateUtil.Guid)
                .SetParameter("embedding", vector is null ? null : QueryVectorText.Format(vector.Vector), NHibernateUtil.StringClob)
                .SetParameter("min_similarity", minSimilarity, NHibernateUtil.Single)
                .SetParameter("pages", pages, NHibernateUtil.Int32)
                .SetParameter("take", take, NHibernateUtil.Int32)
                .SetParameter("per_page", perPage, NHibernateUtil.Int32)
                .ListAsync<object[]>(ct);
        }
        catch (Exception exception) when (SearchSql.IsStatementTimeout(exception) && !ct.IsCancellationRequested)
        {
            throw new ApiException(503, "Hledání podkladů trvalo příliš dlouho. Zkuste otázku zpřesnit.");
        }
        await session.CreateSQLQuery("""
            SELECT set_config('statement_timeout',:timeout,true), set_config('jit',:jit,true), set_config('plan_cache_mode',:plans,true)
            """)
            .SetParameter("timeout", (string)previous[0], NHibernateUtil.String)
            .SetParameter("jit", (string)previous[1], NHibernateUtil.String)
            .SetParameter("plans", (string)previous[2], NHibernateUtil.String)
            .UniqueResultAsync(ct);
        return rows.Select(row => new AnswerSourceRow((short)row[0], (Guid)row[1], (Guid)row[2], (Guid)row[3], (Guid)row[4], (string)row[5],
            (string)row[6], (string?)row[7], (string?)row[8], (string)row[9], (int)row[10])).ToArray();
    }

    /// <summary>The chunk texts of exactly these chunks while they are current: the generation still Active and the revision
    /// still the page's published one (a source text of an older revision is never read again, plan phase 3 R2 point 5). The
    /// body without its context line and without password references, as <see cref="AnswerSourceQueries.Sources"/> returns it.</summary>
    public async Task<IReadOnlyList<AnswerChunkText>> ChunkTextsAsync(IReadOnlyList<ChunkLocator> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0)
        {
            return [];
        }
        var rows = await session.CreateSQLQuery($"""
            SELECT k."GenerationId" AS generation_id,k."PageId" AS page_id,k."RevisionId" AS revision_id,k."Ordinal" AS ordinal,
              {SearchSqlText.StripSecrets(ChunkBody)} AS text
            FROM unnest(CAST(string_to_array(:generations,',') AS uuid[]),CAST(string_to_array(:pages,',') AS uuid[]),
                CAST(string_to_array(:revisions,',') AS uuid[]),CAST(string_to_array(:ordinals,',') AS smallint[])) AS r(g,p,v,o)
              JOIN "KnowledgeChunk" k ON k."GenerationId"=r.g AND k."PageId"=r.p AND k."Ordinal"=r.o AND k."RevisionId"=r.v
              JOIN "IndexGeneration" g ON g."Id"=k."GenerationId" AND g."State"='Active'
              JOIN "Page" p ON p."Id"=k."PageId" AND p."PublishedRevisionId"=k."RevisionId"
            WHERE strpos(k."Text",E'\n')>0
            """)
            .AddScalar("generation_id", NHibernateUtil.Guid)
            .AddScalar("page_id", NHibernateUtil.Guid)
            .AddScalar("revision_id", NHibernateUtil.Guid)
            .AddScalar("ordinal", NHibernateUtil.Int16)
            .AddScalar("text", NHibernateUtil.String)
            .SetParameter("generations", string.Join(',', chunks.Select(x => x.GenerationId)), NHibernateUtil.String)
            .SetParameter("pages", string.Join(',', chunks.Select(x => x.PageId)), NHibernateUtil.String)
            .SetParameter("revisions", string.Join(',', chunks.Select(x => x.RevisionId)), NHibernateUtil.String)
            .SetParameter("ordinals", string.Join(',', chunks.Select(x => x.Ordinal)), NHibernateUtil.String)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new AnswerChunkText((Guid)row[0], (Guid)row[1], (Guid)row[2], (short)row[3], (string)row[4])).ToArray();
    }

    /// <summary>The view of page chunks for <see cref="PageSourceAdapter.DescribeAsync"/>: the title of the page's current published
    /// revision and its space; the chunk's heading only while the chunk's revision is the published one. A page that no longer
    /// exists or is not published has no row. No access decision.</summary>
    public async Task<IReadOnlyList<AnswerChunkDescription>> DescribeAsync(IReadOnlyList<ChunkLocator> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0)
        {
            return [];
        }
        var rows = await session.CreateSQLQuery("""
            SELECT r.g AS generation_id,r.p AS page_id,r.v AS revision_id,r.o AS ordinal,CAST(pr."Title" AS text) AS title,p."SpaceId" AS space_id,
              CAST(s."Name" AS text) AS space_name,
              (SELECT CAST(k."HeadingTitle" AS text) FROM "KnowledgeChunk" k
               WHERE k."GenerationId"=r.g AND k."PageId"=r.p AND k."Ordinal"=r.o AND k."RevisionId"=r.v AND r.v=p."PublishedRevisionId") AS heading_title
            FROM unnest(CAST(string_to_array(:generations,',') AS uuid[]),CAST(string_to_array(:pages,',') AS uuid[]),
                CAST(string_to_array(:revisions,',') AS uuid[]),CAST(string_to_array(:ordinals,',') AS smallint[])) AS r(g,p,v,o)
              JOIN "Page" p ON p."Id"=r.p
              JOIN "PageRevision" pr ON pr."Id"=p."PublishedRevisionId"
              JOIN "Space" s ON s."Id"=p."SpaceId"
            """)
            .AddScalar("generation_id", NHibernateUtil.Guid)
            .AddScalar("page_id", NHibernateUtil.Guid)
            .AddScalar("revision_id", NHibernateUtil.Guid)
            .AddScalar("ordinal", NHibernateUtil.Int16)
            .AddScalar("title", NHibernateUtil.String)
            .AddScalar("space_id", NHibernateUtil.Guid)
            .AddScalar("space_name", NHibernateUtil.String)
            .AddScalar("heading_title", NHibernateUtil.String)
            .SetParameter("generations", string.Join(',', chunks.Select(x => x.GenerationId)), NHibernateUtil.String)
            .SetParameter("pages", string.Join(',', chunks.Select(x => x.PageId)), NHibernateUtil.String)
            .SetParameter("revisions", string.Join(',', chunks.Select(x => x.RevisionId)), NHibernateUtil.String)
            .SetParameter("ordinals", string.Join(',', chunks.Select(x => x.Ordinal)), NHibernateUtil.String)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new AnswerChunkDescription((Guid)row[0], (Guid)row[1], (Guid)row[2], (short)row[3], (string)row[4], (Guid)row[5],
            (string)row[6], (string?)row[7])).ToArray();
    }

    /// <summary>The current space of each existing page, read without a lock (plan phase 3 R2 step 1).</summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> CurrentSpacesAsync(IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        if (pageIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }
        var rows = await session.CreateSQLQuery("""SELECT p."Id" AS id,p."SpaceId" AS space FROM "Page" p WHERE p."Id" IN (:ids)""")
            .AddScalar("id", NHibernateUtil.Guid)
            .AddScalar("space", NHibernateUtil.Guid)
            .SetParameterList("ids", pageIds.Distinct().ToArray())
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => (Guid)row[1]);
    }

    /// <summary>The published revision of each existing published page, read without a lock: the revision an agent turn's source
    /// names. The gate then authorizes it under its locks. No access decision.</summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> PublishedRevisionsAsync(IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        if (pageIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }
        var rows = await session.CreateSQLQuery("""
            SELECT p."Id" AS id,p."PublishedRevisionId" AS revision FROM "Page" p WHERE p."Id" IN (:ids) AND p."PublishedRevisionId" IS NOT NULL
            """)
            .AddScalar("id", NHibernateUtil.Guid)
            .AddScalar("revision", NHibernateUtil.Guid)
            .SetParameterList("ids", pageIds.Distinct().ToArray())
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => (Guid)row[1]);
    }

    public async Task<IReadOnlyList<AnswerSourceStatusRow>> StatusAsync(Guid actor, string stamp, IReadOnlyList<(Guid PageId, Guid RevisionId)> refs,
        CancellationToken ct)
    {
        if (refs.Count == 0)
        {
            return [];
        }
        var rows = await session.CreateSQLQuery($"SELECT page_id,revision_id,available,changed FROM ({AnswerSourceQueries.Status}) status")
            .AddScalar("page_id", NHibernateUtil.Guid)
            .AddScalar("revision_id", NHibernateUtil.Guid)
            .AddScalar("available", NHibernateUtil.Boolean)
            .AddScalar("changed", NHibernateUtil.Boolean)
            .SetParameter("actor", actor, NHibernateUtil.Guid)
            .SetParameter("stamp", stamp, NHibernateUtil.String)
            .SetParameter("pages", "{" + string.Join(',', refs.Select(x => x.PageId)) + "}", NHibernateUtil.String)
            .SetParameter("revisions", "{" + string.Join(',', refs.Select(x => x.RevisionId)) + "}", NHibernateUtil.String)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new AnswerSourceStatusRow((Guid)row[0], (Guid)row[1], (bool)row[2], (bool)row[3])).ToArray();
    }


    /// <summary>Published page chunks in document order, after the preparation authorized and locked that page.</summary>
    public async Task<IReadOnlyList<AuthorizedSource>> SummarySourcesAsync(Guid pageId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($"""
            SELECT k."Ordinal" AS ordinal,k."GenerationId" AS generation,k."RevisionId" AS revision,p."SpaceId" AS space,
              CAST(s."Name" AS text) AS space_name,CAST(r."Title" AS text) AS title,k."HeadingId" AS heading,k."HeadingTitle" AS heading_title,
              {SearchSqlText.StripSecrets(ChunkBody)} AS text
            FROM "Page" p JOIN "Space" s ON s."Id"=p."SpaceId"
              JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
              JOIN "KnowledgeChunk" k ON k."PageId"=p."Id" AND k."RevisionId"=p."PublishedRevisionId"
              JOIN "IndexGeneration" g ON g."Id"=k."GenerationId" AND g."State"='Active'
            WHERE p."Id"=:page AND NOT p."IsTrashed" AND NOT p."IsTemplate" AND {AccessSql.SpaceInAi("p.\"SpaceId\"")}
              AND strpos(k."Text",E'\n')>0
            ORDER BY k."Ordinal"
            """)
            .AddScalar("ordinal", NHibernateUtil.Int16).AddScalar("generation", NHibernateUtil.Guid).AddScalar("revision", NHibernateUtil.Guid)
            .AddScalar("space", NHibernateUtil.Guid).AddScalar("space_name", NHibernateUtil.String).AddScalar("title", NHibernateUtil.String)
            .AddScalar("heading", NHibernateUtil.String).AddScalar("heading_title", NHibernateUtil.String).AddScalar("text", NHibernateUtil.String)
            .SetParameter("page", pageId).ListAsync<object[]>(ct);
        return rows.Select(row => new AuthorizedSource(
            new SourceReference(PageSourceAdapter.SourceType, PageSourceAdapter.SourceVersion, pageId.ToString("N"),
                ((Guid)row[2]).ToString("N"), ((Guid)row[1]).ToString("N") + ":" + (short)row[0]),
            (string)row[5], (string)row[4], (Guid)row[3], (string?)row[7], (string)row[8], 0,
            new ChunkLocator((Guid)row[1], pageId, (Guid)row[2], (short)row[0]), (string?)row[6])).ToArray();
    }

    /// <summary>A chunk's text without its context line.</summary>
    private const string ChunkBody = """substr(k."Text",strpos(k."Text",E'\n')+1)""";

    private void RequireTransaction()
    {
        // set_config(name, value, true) is SET LOCAL: without a transaction it would last one statement.
        if (session.GetCurrentTransaction() is not { IsActive: true })
        {
            throw new InvalidOperationException("Answer sources are read in a transaction.");
        }
    }
}
