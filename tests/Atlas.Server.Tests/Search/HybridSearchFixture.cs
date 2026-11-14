using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.AiIndex.Domain;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Ai.Fakes;
using Wolverine;
using SearchMessage = Atlas.Server.Features.Search.Queries.Search;

namespace Atlas.Server.Tests.Search;

/// <summary>
/// Hybrid search over the real index: <see cref="IndexFixture"/> (fake model, fake clock, A3's real cache and breaker) with an
/// active generation built on the empty wiki, and a reader of the first space only. Pages published afterwards are indexed by
/// <see cref="IndexAsync"/>. Searches go through the endpoints' path (<see cref="SearchEndpoints.SearchAsync"/>): the query
/// vector first, then the handler.
/// </summary>
internal sealed class HybridSearchFixture : IAsyncDisposable
{
    private HybridSearchFixture(IndexFixture index, UserAccount reader, GenerationView generation)
    {
        Index = index;
        Reader = reader;
        Generation = generation;
    }

    public IndexFixture Index { get; }
    public UserAccount Reader { get; }
    public GenerationView Generation { get; }
    public UserAccount Writer => Index.Writer;
    public UserAccount Admin => Index.Admin;
    public FakeAiClientFactory Fake => Index.Fake;
    public ISemanticQuery Semantic
    {
        get
        {
            Index.Host.Actor.Current ??= Reader;
            return Index.Host.Services.GetRequiredService<ISemanticQuery>();
        }
    }

    /// <param name="meaning">The fake's <see cref="FakeAiClientFactory.Meaning"/>: which texts share a concept.</param>
    /// <param name="configure">Extra registrations of the host (e.g. a second source adapter, a paused content access).</param>
    public static async Task<HybridSearchFixture> CreateAsync(Func<string, string>? meaning = null, Action<IServiceCollection>? configure = null)
    {
        var index = await IndexFixture.CreateAsync(configure);
        var reader = await index.Host.SeedUserAsync("reader");
        await index.Host.GrantAiAsync(reader);
        await index.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant
        {
            Id = Guid.NewGuid(), SpaceId = index.SpaceId, UserId = reader.Id, Role = (int)SpaceRole.Reader,
        }));
        if (meaning is not null)
        {
            index.Fake.Meaning = meaning;
        }
        var generation = await index.ActiveIndexAsync();
        return new HybridSearchFixture(index, reader, generation);
    }

    public async Task<Guid> PublishAsync(string title, string markdown, Guid? spaceId = null, params (string Title, string Id)[] headings)
        => (await Index.PublishNewAsync(title, markdown, spaceId, headings)).Id;

    /// <summary>Runs the index worker until the queue is empty.</summary>
    public Task<int> IndexAsync() => Index.RunAsync();

    public async Task<SearchResponse> FindAsync(UserAccount actor, string query, SearchOptions? options = null, int offset = 0, int limit = 20,
        bool fuzzy = true, Guid? spaceId = null)
    {
        Index.Host.Actor.Current = actor;
        using var scope = Index.Host.Services.CreateScope();
        var search = new SearchMessage(query, spaceId, offset, limit, options ?? new SearchOptions()) { Fuzzy = fuzzy };
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().SearchAsync(Semantic, search, default);
    }

    /// <summary>The handler alone with a given vector (or none): what the endpoint would send.</summary>
    public Task<SearchResponse> InvokeAsync(UserAccount actor, string query, SemanticQuery? semantic, SearchOptions? options = null)
        => Index.As<SearchResponse>(actor, new SearchMessage(query, null, 0, 20, options ?? new SearchOptions()) { Semantic = semantic });

    /// <summary>The vector the fake model gives a query (the profile's query prefix included), as the index compares it.</summary>
    public float[] QueryVector(string query) => FakeAi.Vector(Fake.Meaning(IndexFixture.Embeddings().QueryPrefix + query));

    /// <summary>Adds chunks to a page that already has its <c>KnowledgePage</c> row, bypassing the worker. Each chunk is the given
    /// vector nudged in one dimension, so all are distinct (HNSW folds identical vectors into one graph element) and all are
    /// nearer to it than anything else.</summary>
    public async Task AddChunksAsync(Guid pageId, int count, float[] vector)
    {
        await using var connection = new NpgsqlConnection(Index.Host.ConnectionString);
        await connection.OpenAsync();
        for (var i = 0; i < count; i++)
        {
            var nudged = (float[])vector.Clone();
            var n = m_nudges++;
            nudged[n % nudged.Length] += 0.002f * (1 + n / nudged.Length);
            await using var command = new NpgsqlCommand("""
                INSERT INTO "KnowledgeChunk"("GenerationId","PageId","RevisionId","Ordinal","HeadingId","HeadingTitle","Text","TextHash","Embedding")
                SELECT k."GenerationId",k."PageId",k."RevisionId",(100+@i)::smallint,'skryto-'||@i,'Skryto '||@i,'Skrytý text '||@i,
                  sha256(convert_to('skryto'||@i||k."PageId"::text,'UTF8')),CAST(@vector AS vector)
                FROM "KnowledgePage" k WHERE k."GenerationId"=@generation AND k."PageId"=@page
                """, connection);
            command.Parameters.AddWithValue("i", i);
            command.Parameters.AddWithValue("vector", VectorText.Format(nudged));
            command.Parameters.AddWithValue("generation", Generation.Id);
            command.Parameters.AddWithValue("page", pageId);
            if (await command.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException("The page has no KnowledgePage row in the active generation.");
            }
        }
    }

    private int m_nudges;

    /// <summary>Gives a page that was never indexed (a draft) its own <c>KnowledgePage</c> row, for its working revision.</summary>
    public async Task AddKnowledgePageAsync(Guid pageId, float[] vector)
    {
        await using var connection = new NpgsqlConnection(Index.Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO "KnowledgePage"("GenerationId","PageId","RevisionId","IsTemplate","TitleEmbedding","TitleHash","IndexedAt")
            SELECT @generation,r."PageId",r."Id",false,CAST(@vector AS vector),sha256(convert_to(r."Id"::text,'UTF8')),now()
            FROM "PageRevision" r WHERE r."PageId"=@page AND r."ActiveTo" IS NULL
            """, connection);
        command.Parameters.AddWithValue("vector", VectorText.Format(vector));
        command.Parameters.AddWithValue("generation", Generation.Id);
        command.Parameters.AddWithValue("page", pageId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Index.Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs the published hybrid search's query text (SearchQueries.HybridPublished) directly, in one transaction with the settings of the handler plus the
    /// given ones (for example a forced index scan). Returns the page ids in order and the plan of the same call.</summary>
    public async Task<(IReadOnlyList<Guid> Pages, string Plan)> HybridSqlAsync(UserAccount actor, string query, float[] vector, string? stamp = null,
        Guid? generationId = null, string settings = "")
    {
        await using var connection = new NpgsqlConnection(Index.Host.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var prepare = new NpgsqlCommand("""
            SET LOCAL pg_trgm.word_similarity_threshold=0.45; SET LOCAL plan_cache_mode=force_custom_plan; SET LOCAL jit=off;
            SET LOCAL hnsw.iterative_scan=relaxed_order; SET LOCAL hnsw.ef_search=100; SET LOCAL hnsw.max_scan_tuples=20000;
            """ + settings, connection, transaction))
        {
            await prepare.ExecuteNonQueryAsync();
        }
        var normalized = SearchText.Normalize(query);
        var parameters = SearchQueryParameters.Create(query, normalized, actor.Id, stamp ?? actor.SecurityStamp, null);
        var call = "SELECT page_id FROM (" + SearchQueries.HybridPublished + ") moved";
        var pages = new List<Guid>();
        await using (var command = Bind(new NpgsqlCommand(call, connection, transaction)))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                pages.Add(reader.GetGuid(0));
            }
        }
        // EXPLAIN of the query shows the scan of the chunk index.
        var plan = new System.Text.StringBuilder();
        await using (var explain = Bind(new NpgsqlCommand("EXPLAIN (ANALYZE, COSTS OFF) " + call, connection, transaction)))
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(0));
            }
        }
        await transaction.RollbackAsync();
        return (pages, plan.ToString());

        NpgsqlCommand Bind(NpgsqlCommand command)
        {
            command.Parameters.AddWithValue("query", parameters.Query);
            command.Parameters.AddWithValue("prefix", parameters.Prefix);
            command.Parameters.AddWithValue("fts", parameters.Fts);
            command.Parameters.AddWithValue("phrase", parameters.Phrase);
            command.Parameters.AddWithValue("fuzzy", parameters.Fuzzy);
            command.Parameters.AddWithValue("actor", parameters.Actor);
            command.Parameters.AddWithValue("stamp", parameters.Stamp);
            command.Parameters.AddWithValue("take", 51);
            command.Parameters.AddWithValue("skip", 0);
            command.Parameters.Add(new NpgsqlParameter("space", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("changed_since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = DBNull.Value });
            command.Parameters.AddWithValue("latest", false);
            command.Parameters.AddWithValue("min_similarity", 0.45f);
            command.Parameters.AddWithValue("generation", generationId ?? Generation.Id);
            command.Parameters.AddWithValue("embedding", VectorText.Format(vector));
            return command;
        }
    }

    public ValueTask DisposeAsync() => Index.DisposeAsync();
}
