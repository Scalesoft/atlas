using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using System.Collections.Concurrent;
using Atlas.Server;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Identity;
using Atlas.Server.Platform.Persistence;

// The benchmark runs the same query text as production (SearchQueries).
var PublishedCall="SELECT * FROM ("+SearchQueries.Published+") moved";
var connectionString = Environment.GetEnvironmentVariable("ATLAS_TEST_DATABASE");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set ATLAS_TEST_DATABASE to an isolated PostgreSQL 18 test database.");
}
var readerCount = int.TryParse(Environment.GetEnvironmentVariable("ATLAS_BENCHMARK_READERS"), out var requestedReaders) ? Math.Clamp(requestedReaders, 1, 50) : 2;
var includeAliases = Environment.GetEnvironmentVariable("ATLAS_BENCHMARK_ALIASES") == "1";
// Atlas 2.0 V3: also seed a knowledge index with fake vectors and time the hybrid search against the lexical one.
var includeHybrid = Environment.GetEnvironmentVariable("ATLAS_BENCHMARK_HYBRID") == "1";
var settings = new NpgsqlConnectionStringBuilder(connectionString);
var schema = "atlas_search_bench_" + Guid.NewGuid().ToString("N");
await using var admin = new NpgsqlConnection(settings.ConnectionString);
await admin.OpenAsync();
await Execute(admin, $"CREATE SCHEMA {schema}");
try
{
    settings.SearchPath = schema + ",public";
    await using var connection = new NpgsqlConnection(settings.ConnectionString);
    await connection.OpenAsync();
    await Migration(connection, "001_initial.sql");
    var seed = Stopwatch.StartNew();
    await Execute(connection, """
      INSERT INTO "UserAccount" VALUES ('00000000-0000-0000-0000-000000000001','benchmark','Benchmark',false,true,'benchmark');
      INSERT INTO "Space" VALUES ('00000000-0000-0000-0000-000000000002','Engineering','code','blue');
      INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role") VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1);
      INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
        SELECT md5('page-'||g)::uuid,'00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',5 FROM generate_series(1,10000) g;
      INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo")
        SELECT md5('revision-'||g||'-'||v)::uuid, md5('page-'||g)::uuid,v,
          CASE WHEN v=4 THEN 'Původní příručka '||g ELSE (ARRAY['Přístupy do systémů','Autentizace a OIDC','Zálohování databáze','Nasazení Kubernetes','Monitoring serveru','Onboarding kolegů','Incident postup','Vývoj aplikace'])[1+g%8]||' '||g END,
          '{}', repeat((ARRAY['Uživatelské účty a přístupy. Správce nastavuje role a skupiny.','Autentizace používá OpenID Connect a bezpečné přihlášení.','Zálohování databáze PostgreSQL vyžaduje pravidelné ověření obnovy.','Nasazení Kubernetes běží přes CI a kontroluje health endpoint.','Monitoring sleduje odezvu serveru a dostupnost databáze.','Onboarding kolegů zahrnuje vybavení a bezpečnostní školení.','Incident postup eviduje chyby a důvody výpadků.','Vývoj aplikace používá .NET a Vue s automatickými testy.'])[1+g%8]||' ',20),
          '00000000-0000-0000-0000-000000000001','2026-01-01'::timestamptz+v*interval '1 second',CASE WHEN v<5 THEN '2026-01-01'::timestamptz+(v+1)*interval '1 second' ELSE NULL END
        FROM generate_series(1,10000) g CROSS JOIN generate_series(1,5) v ORDER BY g,v;
      UPDATE "Page" SET "PublishedRevisionId"=md5('revision-'||g||'-5')::uuid FROM generate_series(1,10000) g WHERE "Id"=md5('page-'||g)::uuid;
      """);
    const string migrationPrefix = "Atlas.Server.Persistence.Migrations.";
    foreach (var migration in typeof(AtlasDatabase).Assembly.GetManifestResourceNames().Where(name => name.StartsWith(migrationPrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Where(name => !name.EndsWith("001_initial.sql", StringComparison.Ordinal)))
        await Migration(connection, migration[migrationPrefix.Length..]);
    if (includeAliases)
    {
        // Seed only this benchmark's isolated schema with actual published-title history and mapped checkpoints.
        await Execute(connection, """
          INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","ActiveTo","Channel")
            SELECT gen_random_uuid(),"PageId","Id","AuthorId","ActiveFrom","ActiveTo",'web'
            FROM "PageRevision" WHERE "Version"=4;
          INSERT INTO "WikiImportSource"("Id","SourceKey") VALUES('00000000-0000-0000-0000-000000000003','benchmark-aliases');
          INSERT INTO "WikiImportPage"("Id","SourceId","SourcePath","PageId")
            SELECT md5('import-'||g)::uuid,'00000000-0000-0000-0000-000000000003','legacy/route-'||g||'.md',md5('page-'||g)::uuid FROM generate_series(1,1000) g;
          INSERT INTO "WikiImportCheckpoint"("Id","ImportPageId","RevisionId","SourceSha256","ActiveFrom")
            SELECT gen_random_uuid(),m."Id",p."PublishedRevisionId",repeat('a',64),clock_timestamp()
            FROM "WikiImportPage" m JOIN "Page" p ON p."Id"=m."PageId";
          """);
    }
    // Deliberately avoid VACUUM: acceptance includes the immediate post-import state.
    await Execute(connection,"ANALYZE");
    Console.WriteLine($"Seeded 10000 published docs /50000 revisions in {seed.Elapsed.TotalSeconds:0.0}s");
    // Search projections with their indexes and TOAST (the vectors), so lexeme changes show their cost.
    var sizes = new { searchDocumentMb = await Scalar(connection, "SELECT round(pg_total_relation_size('\"SearchDocument\"')/1048576.0,1)"),
        searchDocumentIndexesMb = await Scalar(connection, "SELECT round(pg_indexes_size('\"SearchDocument\"')/1048576.0,1)"),
        workingSearchDocumentMb = await Scalar(connection, "SELECT round(pg_total_relation_size('\"WorkingSearchDocument\"')/1048576.0,1)") };
    Console.WriteLine(JsonSerializer.Serialize(sizes));
    var results = new List<object>();
    // P2-7: the last case is the search's fallback for a question no page has all words of (SearchHandler.RetrieveRelaxedAsync):
    // its significant words OR'ed, the full-text tier only (fuzzy off), as the handler runs it under its 500 ms bound.
    foreach(var (query, relaxed) in new[]{"pristupy","autentikace","nasazeni kube","monitoring serveru 500"}.Concat(includeAliases ? ["puvodni prirucka 500", "legacy route 500"] : Array.Empty<string>())
        .Select(query => (query, false)).Append(("kdo nam dodal zalohovani a monitoring kubernetes", true)))
    {
        var times = new List<double>();
        string explain = "";
        var rowsReturned = 0;
        for(var run=0;run<16;run++)
        {
            await using var tx=await connection.BeginTransactionAsync();
            await using(var threshold=new NpgsqlCommand("SET LOCAL pg_trgm.word_similarity_threshold=0.45; SET LOCAL statement_timeout='10000ms'",connection,tx)) await threshold.ExecuteNonQueryAsync();
            var sql=run==15?"EXPLAIN (ANALYZE, BUFFERS) "+PublishedCall:PublishedCall;
            await using var command=new NpgsqlCommand(sql,connection,tx){CommandTimeout=15};
            var normalized=SearchText.Normalize(query);
            command.Parameters.AddWithValue("query",normalized);command.Parameters.AddWithValue("prefix",normalized+"%");
            command.Parameters.AddWithValue("fts",relaxed ? SearchText.AnyWordExpression(query)! : SearchText.QueryExpression(query).Expression);
            command.Parameters.AddWithValue("fuzzy",!relaxed);command.Parameters.AddWithValue("phrase",false);command.Parameters.AddWithValue("stamp","benchmark");command.Parameters.AddWithValue("space",NpgsqlDbType.Uuid,DBNull.Value);command.Parameters.AddWithValue("take",21);command.Parameters.AddWithValue("skip",0);command.Parameters.AddWithValue("actor",Guid.Parse("00000000-0000-0000-0000-000000000001"));
            var timer=Stopwatch.StartNew();
            await using(var reader=await command.ExecuteReaderAsync())while(await reader.ReadAsync()){ if(run==15)explain+=reader.GetString(0)+Environment.NewLine; else if(run==0)rowsReturned++; }
            if (run==0 && rowsReturned==0)
            {
                throw new InvalidOperationException("Benchmark query returned no matches: " + query);
            }
            timer.Stop();
            await tx.CommitAsync();
            if(run>0&&run<15)
            {
                times.Add(timer.Elapsed.TotalMilliseconds);
            }
        }
        times.Sort();
        var item=new{query,relaxed,rowsReturned,samples=times.Count,p50Ms=Math.Round(times[times.Count/2],2),p95Ms=Math.Round(times[(int)Math.Ceiling(times.Count*.95)-1],2),explain};
        results.Add(item);
        Console.WriteLine(JsonSerializer.Serialize(new{item.query,item.relaxed,item.rowsReturned,item.samples,item.p50Ms,item.p95Ms}));
    }
    var hybrid = includeHybrid ? await HybridBenchmark(connection) : null;
    // Exercise the published search on its own connection with fixed fuzzy/phrase flags
    // while an independent connection refreshes published search projections. The complete read
    // path is a Wolverine message now, so this loop measures the query, not the feature.
    var actor = new UserAccount { Id=Guid.Parse("00000000-0000-0000-0000-000000000001"), SecurityStamp="benchmark", IsActive=true };
    var concurrentTimes = new ConcurrentBag<double>();
    var refreshTimes = new ConcurrentBag<double>();
    var worker = Task.Run(async () => {
        await using var indexing = new NpgsqlConnection(settings.ConnectionString);
        await indexing.OpenAsync();
        for(var i=0;i<40;i++) {
            var timer=Stopwatch.StartNew();
            await using var refresh = new NpgsqlCommand("UPDATE \"SearchDocument\" SET \"RevisionId\"=\"RevisionId\" WHERE \"PageId\" IN (SELECT md5('page-'||g)::uuid FROM generate_series(@start,@start+9) g)",indexing);
            refresh.Parameters.AddWithValue("start",i*10+1);
            await refresh.ExecuteNonQueryAsync(); refreshTimes.Add(timer.Elapsed.TotalMilliseconds);
            await Task.Delay(10);
        }
    });
    var readers = Enumerable.Range(0,readerCount).Select(async reader => {
        for(var i=0;i<24;i++) {
            var timer=Stopwatch.StartNew();
            var rows=await SearchPublished(settings.ConnectionString,actor,i%2==0?"pristupy":"autentikace");
            if(rows==0)
            {
                throw new InvalidOperationException("Concurrent search lost accessible published results.");
            }
            concurrentTimes.Add(timer.Elapsed.TotalMilliseconds);
        }
    }).ToArray();
    await Task.WhenAll(readers.Append(worker));
    var concurrent = new { readers=readerCount, projectionRefreshBatches=40, pagesPerBatch=10, samples=concurrentTimes.Count,
        searchP95Ms=Percentile(concurrentTimes,.95), refreshBatchP95Ms=Percentile(refreshTimes,.95) };
    Console.WriteLine(JsonSerializer.Serialize(concurrent));
    // What a migration recomputing every published projection costs on this corpus.
    var recompute = Stopwatch.StartNew();
    await Execute(connection, "UPDATE \"SearchDocument\" SET \"NormalizedTitle\"=\"NormalizedTitle\"");
    var recomputeAllSeconds = Math.Round(recompute.Elapsed.TotalSeconds, 1);
    Console.WriteLine(JsonSerializer.Serialize(new { recomputeAllSeconds }));
    File.WriteAllText(Environment.GetEnvironmentVariable("ATLAS_BENCHMARK_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory,"search-baseline.json"),JsonSerializer.Serialize(new{utc=DateTime.UtcNow,pages=10000,revisions=50000,publishedHistoricalAliases=includeAliases ? 10000 : 0, importedPathAliases=includeAliases ? 1000 : 0,sizes,results,concurrent,recomputeAllSeconds,hybrid},new JsonSerializerOptions{WriteIndented=true}));
}
finally{if(Environment.GetEnvironmentVariable("ATLAS_BENCHMARK_KEEP")=="1")Console.WriteLine("Kept schema "+schema);else await Execute(admin,$"DROP SCHEMA {schema} CASCADE");}


static async Task<int> SearchPublished(string connectionString,UserAccount actor,string query)
{
    await using var connection=new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction=await connection.BeginTransactionAsync();
    await using(var settings=new NpgsqlCommand("SET LOCAL pg_trgm.word_similarity_threshold=0.45; SET LOCAL statement_timeout='10000ms'",connection,transaction))await settings.ExecuteNonQueryAsync();
    await using var command=new NpgsqlCommand("SELECT * FROM ("+SearchQueries.Published+") moved",connection,transaction){CommandTimeout=15};
    var normalized=SearchText.Normalize(query);
    command.Parameters.AddWithValue("query",normalized);command.Parameters.AddWithValue("prefix",normalized+"%");command.Parameters.AddWithValue("fts",SearchText.QueryExpression(query).Expression);
    command.Parameters.AddWithValue("phrase",false);command.Parameters.AddWithValue("fuzzy",true);command.Parameters.AddWithValue("actor",actor.Id);command.Parameters.AddWithValue("stamp",actor.SecurityStamp);
    command.Parameters.AddWithValue("space",NpgsqlDbType.Uuid,DBNull.Value);command.Parameters.AddWithValue("take",21);command.Parameters.AddWithValue("skip",0);
    var rows=0;
    await using(var reader=await command.ExecuteReaderAsync())while(await reader.ReadAsync())rows++;
    await transaction.CommitAsync();
    return rows;
}

// V3 (plan §11): 10 000 published pages, 25 000 chunks with fake vectors (8 topic centres + noise, like the corpus' 8 topics),
// the HNSW index of migration 2026100540 built over them, then the published and the unified scope lexically and hybrid with
// the transaction settings of SearchSql.PrepareAsync. A query vector is its topic centre + noise (cosine ~0.67 to the topic's
// chunks, so all 200 candidates pass the 0.45 threshold: the heavy case). Latency only; the embedding call is not part of it.
static async Task<object> HybridBenchmark(NpgsqlConnection connection)
{
    const string generation = "00000000-0000-0000-0000-0000000000a2";
    var seed = Stopwatch.StartNew();
    await Execute(connection, $$"""
      SELECT setseed(0.42);
      INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","Dimensions","Fingerprint","CreatedById","CreatedAt")
        VALUES ('00000000-0000-0000-0000-0000000000a1','embeddings',true,'openai-compatible','company','http://bench/v1','fake',30,4,1024,repeat('f',64),'00000000-0000-0000-0000-000000000001',now());
      INSERT INTO "IndexGeneration"("Id","State","ProfileRevisionId","Fingerprint","ChunkingVersion","Dimensions","CreatedById","CreatedAt","BoundaryAt","ActivatedAt","Version")
        VALUES ('{{generation}}','Active','00000000-0000-0000-0000-0000000000a1',repeat('f',64),'headings-1800@1',1024,'00000000-0000-0000-0000-000000000001',now(),now(),now(),now());
      CREATE TABLE bench_topic AS SELECT t, ARRAY(SELECT (random()-0.5)::real FROM generate_series(1,1024) WHERE t>=0) AS center FROM generate_series(0,7) t;
      DROP INDEX "IX_KnowledgeChunk_Embedding";
      INSERT INTO "KnowledgePage"("GenerationId","PageId","RevisionId","IsTemplate","TitleEmbedding","TitleHash","IndexedAt")
        SELECT '{{generation}}',md5('page-'||g)::uuid,md5('revision-'||g||'-5')::uuid,false,l2_normalize(center::vector),sha256(convert_to('t'||g,'UTF8')),now()
        FROM generate_series(1,10000) g JOIN bench_topic ON t=g%8;
      INSERT INTO "KnowledgeChunk"("GenerationId","PageId","RevisionId","Ordinal","HeadingId","HeadingTitle","Text","TextHash","Embedding")
        SELECT '{{generation}}',md5('page-'||g)::uuid,md5('revision-'||g||'-5')::uuid,c,'sekce-'||c,'Sekce '||c,'Stránka '||g||' › Sekce '||c||chr(10)||'Text sekce '||c||' stránky '||g||'.',
          sha256(convert_to('c'||g||'-'||c,'UTF8')),
          l2_normalize(ARRAY(SELECT (x+0.8*(random()-0.5))::real FROM unnest(center) x WHERE g>0 AND c>=0)::vector)
        FROM generate_series(1,10000) g CROSS JOIN LATERAL generate_series(0,1+g%2) c JOIN bench_topic ON t=g%8;
      """);
    var seeded = seed.Elapsed.TotalSeconds;
    var build = Stopwatch.StartNew();
    await Execute(connection, """
      SET maintenance_work_mem='1GB';
      CREATE INDEX "IX_KnowledgeChunk_Embedding" ON "KnowledgeChunk" USING hnsw ("Embedding" vector_cosine_ops) WITH (m = 16, ef_construction = 64);
      RESET maintenance_work_mem;
      ANALYZE;
      """);
    var built = build.Elapsed.TotalSeconds;
    var chunks = await Scalar(connection, "SELECT count(*) FROM \"KnowledgeChunk\"");
    Console.WriteLine($"Seeded {chunks} chunks in {seeded:0.0}s, HNSW index in {built:0.0}s");
    const string settings = """
      SET LOCAL statement_timeout='10000ms'; SET LOCAL pg_trgm.word_similarity_threshold=0.45; SET LOCAL pg_trgm.similarity_threshold=0.45;
      SET LOCAL plan_cache_mode=force_custom_plan; SET LOCAL jit=off;
      SET LOCAL hnsw.iterative_scan=relaxed_order; SET LOCAL hnsw.ef_search=100; SET LOCAL hnsw.max_scan_tuples=20000;
      """;
    var calls = new (string Name, string Sql)[]
    {
        ("published-lexical", $"SELECT page_id FROM ({SearchQueries.Published}) moved"),
        ("published-hybrid", $"SELECT page_id FROM ({SearchQueries.HybridPublished}) moved"),
        ("all-lexical", $"SELECT page_id FROM ({SearchQueries.All}) moved"),
        ("all-hybrid", $"SELECT page_id FROM ({SearchQueries.HybridAll}) moved"),
    };
    var results = new List<object>();
    var exactTitleKept = true;
    // Why SearchSql sends the query vector as scaled integers (QueryVectorText): the ~12.6 kB round-trip text (and the 8.2 kB
    // binary real[]) exceed Npgsql's 8 kB write buffer, and behind a forwarding proxy (Podman, WSL) the split write waits for a
    // delayed ACK.
    var probe = (string)(await Scalar(connection, "SELECT l2_normalize(center::vector)::text FROM bench_topic WHERE t=0"))!;
    var vectorRoundTripP50Ms = new Dictionary<string, double>();
    foreach (var (form, value) in new (string, object)[] { ("text", probe), ("binary", Atlas.Server.Features.AiIndex.Domain.VectorText.Parse(probe)),
        ("scaled", QueryVectorText.Format(Atlas.Server.Features.AiIndex.Domain.VectorText.Parse(probe))) })
    {
        var transport = new List<double>();
        for (var run = 0; run < 15; run++)
        {
            await using var command = new NpgsqlCommand("SELECT CAST(@embedding AS vector) IS NOT NULL", connection);
            command.Parameters.Add(Embedding(value));
            var timer = Stopwatch.StartNew();
            await command.ExecuteScalarAsync();
            if (run > 0) transport.Add(timer.Elapsed.TotalMilliseconds);
        }
        vectorRoundTripP50Ms[form] = Percentile(transport, .5);
    }
    Console.WriteLine("Round trip with the query vector alone, p50 ms: " + JsonSerializer.Serialize(vectorRoundTripP50Ms));
    // The corpus' topic of each query (title index 1+g%8 in the seed); the last query matches no word, only its meaning.
    foreach (var (query, topic) in new[] { ("pristupy", 0), ("autentikace", 1), ("nasazeni kube", 3), ("monitoring serveru 500", 4), ("zotaveni po havarii", 2) })
    {
        var embedding = (string)(await Scalar(connection, $"SELECT l2_normalize(ARRAY(SELECT (x+0.6*(random()-0.5))::real FROM unnest(center) x)::vector)::text FROM bench_topic WHERE t={topic}"))!;
        var firstHits = new Dictionary<string, Guid?>();
        foreach (var (name, sql) in calls)
        {
            var times = new List<double>();
            var server = new List<double>();
            var explain = "";
            var rows = 0;
            // Runs 1–14 are timed at the client, 16–29 on the server (EXPLAIN ANALYZE without timing: planning + execution). The
            // client time of the hybrid calls carries the ~12 kB vector literal through the test host's port forwarding.
            for (var run = 0; run < 30; run++)
            {
                await using var tx = await connection.BeginTransactionAsync();
                await using (var prepare = new NpgsqlCommand(settings, connection, tx)) await prepare.ExecuteNonQueryAsync();
                var prefix = run == 15 ? "EXPLAIN (ANALYZE, BUFFERS) " : run > 15 ? "EXPLAIN (ANALYZE, TIMING OFF, SUMMARY ON) " : "";
                await using var command = new NpgsqlCommand(prefix + sql, connection, tx) { CommandTimeout = 15 };
                var normalized = SearchText.Normalize(query);
                var expression = SearchText.QueryExpression(query);
                command.Parameters.AddWithValue("query", normalized); command.Parameters.AddWithValue("prefix", normalized + "%");
                command.Parameters.AddWithValue("fts", expression.Expression); command.Parameters.AddWithValue("phrase", expression.HasPhrase);
                command.Parameters.AddWithValue("fuzzy", true); command.Parameters.AddWithValue("stamp", "benchmark");
                command.Parameters.AddWithValue("actor", Guid.Parse("00000000-0000-0000-0000-000000000001"));
                command.Parameters.Add(Embedding(QueryVectorText.Format(Atlas.Server.Features.AiIndex.Domain.VectorText.Parse(embedding))));
                command.Parameters.AddWithValue("space", NpgsqlDbType.Uuid, DBNull.Value); command.Parameters.AddWithValue("take", 51);
                command.Parameters.AddWithValue("skip", 0); command.Parameters.AddWithValue("changed_since", NpgsqlDbType.TimestampTz, DBNull.Value);
                command.Parameters.AddWithValue("latest", false); command.Parameters.AddWithValue("generation", Guid.Parse(generation));
                command.Parameters.AddWithValue("min_similarity", 0.45f);
                var timer = Stopwatch.StartNew();
                await using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        if (run == 15) explain += reader.GetString(0) + Environment.NewLine;
                        else if (run > 15 && System.Text.RegularExpressions.Regex.Match(reader.GetString(0), @"^(Planning|Execution) Time: ([0-9.]+) ms") is { Success: true } summary)
                        {
                            if (summary.Groups[1].Value == "Planning") server.Add(0);
                            server[^1] += double.Parse(summary.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else if (run == 0) { if (rows++ == 0) firstHits[name] = reader.GetGuid(0); }
                    }
                }
                timer.Stop();
                await tx.CommitAsync();
                if (run > 0 && run < 15) times.Add(timer.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            var item = new { query, scope = name, rowsReturned = rows, samples = times.Count, p50Ms = Math.Round(times[times.Count / 2], 2),
                p95Ms = Math.Round(times[(int)Math.Ceiling(times.Count * .95) - 1], 2), serverP50Ms = Percentile(server, .5), serverP95Ms = Percentile(server, .95),
                hnswIndexScan = explain.Contains("IX_KnowledgeChunk_Embedding", StringComparison.Ordinal), explain };
            results.Add(item);
            Console.WriteLine(JsonSerializer.Serialize(new { item.query, item.scope, item.rowsReturned, item.p50Ms, item.p95Ms, item.serverP50Ms, item.serverP95Ms, item.hnswIndexScan }));
        }
        // An exact title stays the first hit (S1 tiers): the hybrid order may not move it.
        if (query == "monitoring serveru 500")
        {
            exactTitleKept = firstHits.GetValueOrDefault("published-lexical") == firstHits.GetValueOrDefault("published-hybrid")
                && firstHits.GetValueOrDefault("all-lexical") == firstHits.GetValueOrDefault("all-hybrid");
            if (!exactTitleKept) throw new InvalidOperationException("The hybrid search moved an exact title from the first place.");
        }
    }
    return new { pages = 10000, chunks, seedSeconds = Math.Round(seeded, 1), hnswBuildSeconds = Math.Round(built, 1), vectorRoundTripP50Ms, exactTitleKept, results };
}

static NpgsqlParameter Embedding(object value) => value is float[] vector
    ? new NpgsqlParameter("embedding", NpgsqlDbType.Array | NpgsqlDbType.Real) { Value = vector }
    : new NpgsqlParameter("embedding", value);

static async Task<object?> Scalar(NpgsqlConnection connection, string sql) { await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 }; return await command.ExecuteScalarAsync(); }

static async Task Execute(NpgsqlConnection connection,string sql){await using var cmd=new NpgsqlCommand(sql,connection){CommandTimeout=600};await cmd.ExecuteNonQueryAsync();}
static async Task Migration(NpgsqlConnection connection,string name){await using var stream=typeof(AtlasDatabase).Assembly.GetManifestResourceStream("Atlas.Server.Persistence.Migrations."+name)!;using var reader=new StreamReader(stream);await Execute(connection,await reader.ReadToEndAsync());}

static double Percentile(IEnumerable<double> values,double percentile){var ordered=values.Order().ToArray();return Math.Round(ordered[(int)Math.Ceiling(ordered.Length*percentile)-1],2);}
