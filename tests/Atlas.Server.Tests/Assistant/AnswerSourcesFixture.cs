using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.AiIndex.Domain;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Search;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// The sources of answers over the real index: <see cref="HybridSearchFixture"/> (fake model and clock, an active generation, a
/// reader of the first space), the registry reached through <see cref="AnswerSourceProbeHandler"/>, and the question vector made
/// as the endpoint makes it (<see cref="ISemanticQuery"/> with the answer budget).
/// </summary>
internal sealed class AnswerSourcesFixture : IAsyncDisposable
{
    private int m_nudges;

    private AnswerSourcesFixture(HybridSearchFixture search)
    {
        Search = search;
    }

    public HybridSearchFixture Search { get; }
    public IndexFixture Index => Search.Index;
    public UserAccount Reader => Search.Reader;
    public UserAccount Writer => Search.Writer;
    public UserAccount Admin => Search.Admin;
    public GenerationView Generation => Search.Generation;
    public FakeAiClientFactory Fake => Search.Fake;

    public static async Task<AnswerSourcesFixture> CreateAsync(Func<string, string>? meaning = null, Action<IServiceCollection>? configure = null)
        => new(await HybridSearchFixture.CreateAsync(meaning, configure));

    public Task<Guid> PublishAsync(string title, string markdown, Guid? spaceId = null, params (string Title, string Id)[] headings)
        => Search.PublishAsync(title, markdown, spaceId, headings);

    public Task<int> IndexAsync() => Search.IndexAsync();

    public Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters) => Search.ExecuteAsync(sql, parameters);

    /// <summary>The question's vector within the answer budget (null when the embeddings do not answer in time).</summary>
    public async Task<SemanticQuery?> EmbedAsync(string question, UserAccount? actor = null)
    {
        Index.Host.Actor.Current = actor ?? Reader;
        return await Search.Semantic.TryEmbedAsync(question, SemanticQueryService.AnswerBudget, default);
    }

    /// <summary>A retrieval as the preparation makes it: the vector first (unless <paramref name="semantic"/> is false), then the
    /// transaction.</summary>
    public async Task<AnswerSourceSet> RetrieveAsync(UserAccount actor, string question, AnswerScope? scope = null, bool semantic = true,
        int take = 6, int perSource = 2)
    {
        var vector = semantic ? await EmbedAsync(question, actor) : null;
        return await Index.As<AnswerSourceSet>(actor, new RetrieveAnswerSources(question, scope ?? AnswerScope.Wiki, vector, take, perSource));
    }

    public Task<AnswerSourceSet> RetrieveWithAsync(UserAccount actor, string question, SemanticQuery? vector, AnswerScope? scope = null)
        => Index.As<AnswerSourceSet>(actor, new RetrieveAnswerSources(question, scope ?? AnswerScope.Wiki, vector));

    public Task<IReadOnlyDictionary<SourceReference, string?>> LoadTextAsync(UserAccount actor, IReadOnlyList<SourceReference> refs)
        => Index.As<IReadOnlyDictionary<SourceReference, string?>>(actor, new LoadAnswerSourceTexts(refs));

    public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> AuthorizeAsync(UserAccount actor, IReadOnlyList<SourceReference> refs,
        bool requireCurrentRevision)
        => Index.As<IReadOnlyDictionary<SourceReference, SourceStatus>>(actor, new AuthorizeAnswerSources(refs, requireCurrentRevision));

    public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> StatusAsync(UserAccount actor, IReadOnlyList<SourceReference> refs)
        => Index.As<IReadOnlyDictionary<SourceReference, SourceStatus>>(actor, new ReadAnswerSourceStatus(refs));

    /// <summary>A registry outside any transaction, for the column mapping (no SQL).</summary>
    public SourceRegistry Registry(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<SourceRegistry>();

    /// <summary>Adds citable chunks ("{title} › {heading}\n{body}") to a page that has its <c>KnowledgePage</c> row in the active
    /// generation, bypassing the worker. Each is the given vector nudged in one dimension (HNSW folds identical vectors), all
    /// nearer to it than anything else; their text would be returned if any rule let them through.</summary>
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
                SELECT k."GenerationId",k."PageId",k."RevisionId",(100+@i)::smallint,'skryto-'||@i,'Skryto '||@i,
                  'Skrytá › Skryto '||@i||E'\nSKRYTÝ-TEXT '||@i,sha256(convert_to('skryto'||@i||k."PageId"::text,'UTF8')),CAST(@vector AS vector)
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

    /// <summary>Runs the answer sources' query text (AnswerSourceQueries.Sources) directly, in one transaction with the settings of AnswerSourceSql plus the given ones
    /// (for example a forced index scan). Returns the page ids of the rows in order and the plan of the same call.</summary>
    public async Task<(IReadOnlyList<Guid> Pages, string Plan)> AnswerSqlAsync(UserAccount actor, string question, float[]? vector,
        string? stamp = null, string settings = "")
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
        var text = AnswerQueryText.Create(question)!;
        var call = $"SELECT page_id FROM ({AnswerSourceQueries.Sources}) sources";
        var pages = new List<Guid>();
        await using (var command = Bind(new NpgsqlCommand(call, connection, transaction)))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                pages.Add(reader.GetGuid(0));
            }
        }
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
            command.Parameters.AddWithValue("actor", actor.Id);
            command.Parameters.AddWithValue("stamp", stamp ?? actor.SecurityStamp);
            command.Parameters.AddWithValue("query", text.Query);
            command.Parameters.AddWithValue("prefix", text.Prefix);
            command.Parameters.AddWithValue("fts", text.Fts);
            command.Parameters.Add(new NpgsqlParameter("generation", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = vector is null ? DBNull.Value : Generation.Id });
            command.Parameters.Add(new NpgsqlParameter("embedding", NpgsqlTypes.NpgsqlDbType.Text) { Value = vector is null ? DBNull.Value : VectorText.Format(vector) });
            command.Parameters.Add(new NpgsqlParameter("space", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("page", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value });
            command.Parameters.AddWithValue("phrase", false);
            command.Parameters.AddWithValue("min_similarity", 0.45f);
            command.Parameters.AddWithValue("pages", 8);
            command.Parameters.AddWithValue("take", 6);
            command.Parameters.AddWithValue("per_page", 2);
            return command;
        }
    }

    public ValueTask DisposeAsync() => Search.DisposeAsync();
}
