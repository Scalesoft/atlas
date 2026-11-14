using System.Text.Json;
using NHibernate;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.AiIndex.Domain;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Identity;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AiIndex;

[FluentMigrator.Migration(2026110950, "The index's eligibility, the page-neighbour lists and the catalog's resource list are code; their functions go")]
public sealed class LeftoverFunctionsToCodeMigration() : EmbeddedSchemaMigration("2026110950_leftover_functions_to_code.sql");

/// <summary>
/// The only SQL of the knowledge index (plan §10). Vectors travel as pgvector text (<see cref="VectorText"/>), no Npgsql plugin.
/// Eligibility is <see cref="Candidate"/> plus a space included in AI (<see cref="AccessSql.SpaceInAi"/>), evaluated once per space
/// (<see cref="Eligible"/>), never per chunk. Callers own the transaction (one Wolverine message each).
/// </summary>
public sealed class IndexStore(ISession session)
{
    /// <summary>The one definition of "the page belongs in the index" apart from the space: published and not in the trash. Templates
    /// belong too, with their title only (O19). <paramref name="page"/> is the alias of the "Page" row.</summary>
    internal static string Candidate(string page) => $"""({page}."PublishedRevisionId" IS NOT NULL AND NOT {page}."IsTrashed")""";

    /// <summary>Candidate and in an included space, for one page row.</summary>
    internal static string EligibleRow(string page) => $"({Candidate(page)} AND {AccessSql.SpaceInAi($"{page}.\"SpaceId\"")})";

    /// <summary>Eligible pages now, as a CTE: the inclusion of a space is decided once per space.</summary>
    private static readonly string Eligible = $"""
        included AS MATERIALIZED (SELECT s."Id" FROM "Space" s WHERE {AccessSql.SpaceInAi("s.\"Id\"")}),
        eligible AS MATERIALIZED (
          SELECT p."Id",p."PublishedRevisionId",p."IsTemplate" FROM "Page" p
          WHERE {Candidate("p")} AND p."SpaceId" IN (SELECT "Id" FROM included))
        """;

    private const string Live = "('Building','Active')";

    // ---- generations ---------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<IndexGenerationRow>> GenerationsAsync(IReadOnlyCollection<string> states, CancellationToken ct)
    {
        var rows = await GenerationQuery("""WHERE "State" IN (:states) ORDER BY "CreatedAt" DESC""")
            .SetParameterList("states", states).ListAsync<object[]>(ct);
        return rows.Select(ToGeneration).ToArray();
    }

    public async Task<IndexGenerationRow?> GenerationAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        var rows = await GenerationQuery("""WHERE "Id"=:id""" + (forUpdate ? " FOR UPDATE" : "")).SetParameter("id", id).ListAsync<object[]>(ct);
        return rows.Count == 0 ? null : ToGeneration(rows[0]);
    }

    private ISQLQuery GenerationQuery(string where) => session.CreateSQLQuery("""
            SELECT "Id","State","ProfileRevisionId","Fingerprint","ChunkingVersion","Dimensions","CreatedAt","BoundaryAt","ActivatedAt","EndedAt","Version"
            FROM "IndexGeneration"
            """ + " " + where)
        .AddScalar("Id", NHibernateUtil.Guid).AddScalar("State", NHibernateUtil.String).AddScalar("ProfileRevisionId", NHibernateUtil.Guid)
        .AddScalar("Fingerprint", NHibernateUtil.String).AddScalar("ChunkingVersion", NHibernateUtil.String).AddScalar("Dimensions", NHibernateUtil.Int16)
        .AddScalar("CreatedAt", NHibernateUtil.UtcDateTime).AddScalar("BoundaryAt", NHibernateUtil.UtcDateTime)
        .AddScalar("ActivatedAt", NHibernateUtil.UtcDateTime).AddScalar("EndedAt", NHibernateUtil.UtcDateTime).AddScalar("Version", NHibernateUtil.UtcDateTime);

    private static IndexGenerationRow ToGeneration(object[] row) => new((Guid)row[0], (string)row[1], (Guid)row[2], (string)row[3], (string)row[4],
        (short)row[5], (DateTime)row[6], (DateTime)row[7], (DateTime?)row[8], (DateTime?)row[9], (DateTime)row[10]);

    /// <summary>A new Building generation and, in the same statement batch, one task for every eligible page (templates included),
    /// enqueued exactly at the generation's boundary.</summary>
    public async Task<Guid> StartAsync(Guid profileRevisionId, string fingerprint, string chunkingVersion, Guid actorId, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await session.CreateSQLQuery("""
            WITH boundary AS (SELECT clock_timestamp() AS at)
            INSERT INTO "IndexGeneration"("Id","State","ProfileRevisionId","Fingerprint","ChunkingVersion","Dimensions","CreatedById","CreatedAt","BoundaryAt","Version")
            SELECT :id,'Building',:revision,:fingerprint,:chunking,1024,:actor,b.at,b.at,b.at FROM boundary b
            """)
            .SetParameter("id", id).SetParameter("revision", profileRevisionId).SetParameter("fingerprint", fingerprint)
            .SetParameter("chunking", chunkingVersion).SetParameter("actor", actorId)
            .ExecuteUpdateAsync(ct);
        await session.CreateSQLQuery("WITH " + Eligible + """
            INSERT INTO "IndexTask"("GenerationId","PageId","EnqueuedAt","NextAttemptAt")
            SELECT g."Id",e."Id",g."BoundaryAt",now() FROM eligible e CROSS JOIN "IndexGeneration" g WHERE g."Id"=:id
            """).SetParameter("id", id).ExecuteUpdateAsync(ct);
        return id;
    }

    /// <summary>Building → Active, retiring the previous Active one (its tasks go: only live generations are maintained). The
    /// activated generation's neighbour lists become due for a full recompute (no refresh time), not by comparing the database's
    /// activation time with the maintenance's clock.</summary>
    public async Task ActivateAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        await session.CreateSQLQuery("""
            DELETE FROM "IndexTask" t USING "IndexGeneration" g WHERE g."Id"=t."GenerationId" AND g."State"='Active';
            UPDATE "IndexGeneration" SET "State"='Retired',"EndedAt"=clock_timestamp(),"Version"=clock_timestamp() WHERE "State"='Active';
            UPDATE "IndexGeneration" SET "State"='Active',"ActivatedAt"=clock_timestamp(),"ActivatedById"=:actor,"NeighborsRefreshedAt"=NULL,
              "Version"=clock_timestamp() WHERE "Id"=:id;
            """).SetParameter("id", id).SetParameter("actor", actorId).ExecuteUpdateAsync(ct);
    }

    /// <summary>Building → Failed; its tasks go, its rows stay until the maintenance purges them. The active index is untouched.</summary>
    public async Task CancelAsync(Guid id, CancellationToken ct)
    {
        await session.CreateSQLQuery("""
            DELETE FROM "IndexTask" WHERE "GenerationId"=:id;
            UPDATE "IndexGeneration" SET "State"='Failed',"EndedAt"=clock_timestamp(),"Version"=clock_timestamp() WHERE "Id"=:id;
            """).SetParameter("id", id).ExecuteUpdateAsync(ct);
    }

    /// <summary>Removes one batch of rows of dead generations (Failed, or Retired before <paramref name="retiredBefore"/>), then the
    /// generations left without rows. Small batches, so no long transaction.</summary>
    public async Task<(int Pages, int Generations)> PurgeBatchAsync(DateTime retiredBefore, int batch, CancellationToken ct)
    {
        const string dead = """
            SELECT "Id" FROM "IndexGeneration" WHERE "State"='Failed' OR ("State"='Retired' AND "EndedAt"<:before)
            """;
        var pages = await session.CreateSQLQuery($"""
            WITH victims AS (SELECT k."GenerationId",k."PageId" FROM "KnowledgePage" k WHERE k."GenerationId" IN ({dead}) LIMIT :batch)
            DELETE FROM "KnowledgePage" k USING victims v WHERE k."GenerationId"=v."GenerationId" AND k."PageId"=v."PageId"
            """).SetParameter("before", retiredBefore, NHibernateUtil.UtcDateTime).SetParameter("batch", batch).ExecuteUpdateAsync(ct);
        if (pages > 0)
        {
            return (pages, 0);
        }
        var generations = await session.CreateSQLQuery($"""
            DELETE FROM "IndexGeneration" g WHERE g."Id" IN ({dead})
              AND NOT EXISTS (SELECT 1 FROM "KnowledgePage" k WHERE k."GenerationId"=g."Id")
            """).SetParameter("before", retiredBefore, NHibernateUtil.UtcDateTime).ExecuteUpdateAsync(ct);
        return (0, generations);
    }

    // ---- statistics ----------------------------------------------------------------------------------------------------

    public async Task<GenerationStats> StatsAsync(Guid generationId, CancellationToken ct)
    {
        var row = await session.CreateSQLQuery("WITH " + Eligible + """
            , indexed AS MATERIALIZED (
              SELECT e."Id" FROM eligible e JOIN "KnowledgePage" k ON k."GenerationId"=:g AND k."PageId"=e."Id"
                AND k."RevisionId"=e."PublishedRevisionId" AND k."IsTemplate"=e."IsTemplate")
            SELECT (SELECT count(*) FROM eligible) AS eligible,(SELECT count(*) FROM indexed) AS indexed,
              (SELECT count(*) FROM "KnowledgeChunk" c WHERE c."GenerationId"=:g) AS chunks,
              (SELECT count(*) FROM "IndexTask" t WHERE t."GenerationId"=:g) AS pending,
              (SELECT count(*) FROM "IndexTask" t WHERE t."GenerationId"=:g AND t."Attempts">0) AS failing
            """)
            .AddScalar("eligible", NHibernateUtil.Int64).AddScalar("indexed", NHibernateUtil.Int64).AddScalar("chunks", NHibernateUtil.Int64)
            .AddScalar("pending", NHibernateUtil.Int64).AddScalar("failing", NHibernateUtil.Int64)
            .SetParameter("g", generationId).UniqueResultAsync<object[]>(ct);
        return new((int)(long)row[0], (int)(long)row[1], (int)(long)row[2], (int)(long)row[3], (int)(long)row[4]);
    }

    /// <summary>Eligible pages not indexed with their current revision (missing or stale), by published title.</summary>
    public async Task<IReadOnlyList<MissingPage>> MissingAsync(Guid generationId, int take, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("WITH " + Eligible + """
            SELECT e."Id",r."Title" FROM eligible e JOIN "PageRevision" r ON r."Id"=e."PublishedRevisionId"
            WHERE NOT EXISTS (SELECT 1 FROM "KnowledgePage" k WHERE k."GenerationId"=:g AND k."PageId"=e."Id"
              AND k."RevisionId"=e."PublishedRevisionId" AND k."IsTemplate"=e."IsTemplate")
            ORDER BY r."Title",e."Id" LIMIT :take
            """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("Title", NHibernateUtil.String)
            .SetParameter("g", generationId).SetParameter("take", take).ListAsync<object[]>(ct);
        return rows.Select(row => new MissingPage((Guid)row[0], (string)row[1])).ToArray();
    }

    /// <summary>Completeness (plan §10): tasks enqueued at or before the boundary that are left, eligible pages without a row, and
    /// rows of an older revision or of pages that no longer belong.</summary>
    public async Task<(int BoundaryTasks, int Missing, int Stale)> CompletenessAsync(Guid generationId, CancellationToken ct)
    {
        var row = await session.CreateSQLQuery("WITH " + Eligible + """
            SELECT (SELECT count(*) FROM "IndexTask" t JOIN "IndexGeneration" g ON g."Id"=t."GenerationId"
                      WHERE t."GenerationId"=:g AND t."EnqueuedAt"<=g."BoundaryAt") AS boundary,
              (SELECT count(*) FROM eligible e WHERE NOT EXISTS (
                  SELECT 1 FROM "KnowledgePage" k WHERE k."GenerationId"=:g AND k."PageId"=e."Id")) AS missing,
              (SELECT count(*) FROM "KnowledgePage" k LEFT JOIN eligible e ON e."Id"=k."PageId"
                  WHERE k."GenerationId"=:g AND (e."Id" IS NULL OR k."RevisionId"<>e."PublishedRevisionId" OR k."IsTemplate"<>e."IsTemplate")) AS stale
            """)
            .AddScalar("boundary", NHibernateUtil.Int64).AddScalar("missing", NHibernateUtil.Int64).AddScalar("stale", NHibernateUtil.Int64)
            .SetParameter("g", generationId).UniqueResultAsync<object[]>(ct);
        return ((int)(long)row[0], (int)(long)row[1], (int)(long)row[2]);
    }

    public async Task<IndexQueue> QueueAsync(CancellationToken ct)
    {
        var row = await session.CreateSQLQuery($"""
            SELECT count(*) AS pending,count(*) FILTER (WHERE t."Attempts">0) AS failing,min(t."EnqueuedAt") AS oldest
            FROM "IndexTask" t JOIN "IndexGeneration" g ON g."Id"=t."GenerationId" WHERE g."State" IN {Live}
            """)
            .AddScalar("pending", NHibernateUtil.Int64).AddScalar("failing", NHibernateUtil.Int64).AddScalar("oldest", NHibernateUtil.UtcDateTime)
            .UniqueResultAsync<object[]>(ct);
        return new((int)(long)row[0], (int)(long)row[1], (DateTime?)row[2]);
    }

    // ---- consistency ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Enqueues what the live generations (or one of them) disagree with the wiki about: eligible pages without a row, rows of an
    /// older revision or template state, rows of pages that no longer belong (the worker deletes those). A page that already
    /// has a task keeps it (and its backoff). Returns what it had to enqueue, by kind.
    /// </summary>
    public async Task<(int Missing, int Stale, int Orphaned)> EnqueueInconsistentAsync(Guid? generationId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($"""
            WITH gens AS MATERIALIZED (SELECT "Id" FROM "IndexGeneration" WHERE "State" IN {Live} AND (CAST(:g AS uuid) IS NULL OR "Id"=:g)),
            """ + Eligible + """
            , found AS MATERIALIZED (
              SELECT g."Id" AS gen,e."Id" AS page,'missing' AS kind FROM gens g CROSS JOIN eligible e
                WHERE NOT EXISTS (SELECT 1 FROM "KnowledgePage" k WHERE k."GenerationId"=g."Id" AND k."PageId"=e."Id")
              UNION ALL
              SELECT k."GenerationId",k."PageId",CASE WHEN e."Id" IS NULL THEN 'orphaned' ELSE 'stale' END
                FROM "KnowledgePage" k JOIN gens g ON g."Id"=k."GenerationId" LEFT JOIN eligible e ON e."Id"=k."PageId"
                WHERE e."Id" IS NULL OR k."RevisionId"<>e."PublishedRevisionId" OR k."IsTemplate"<>e."IsTemplate"),
            inserted AS (
              INSERT INTO "IndexTask"("GenerationId","PageId","EnqueuedAt","NextAttemptAt")
              SELECT f.gen,f.page,clock_timestamp(),now() FROM found f WHERE EXISTS (SELECT 1 FROM "Page" p WHERE p."Id"=f.page)
              ON CONFLICT ("GenerationId","PageId") DO NOTHING
              RETURNING "GenerationId","PageId")
            SELECT f.kind,count(*) AS n FROM found f JOIN inserted i ON i."GenerationId"=f.gen AND i."PageId"=f.page GROUP BY f.kind
            """)
            .AddScalar("kind", NHibernateUtil.String).AddScalar("n", NHibernateUtil.Int64)
            .SetParameter("g", generationId, NHibernateUtil.Guid).ListAsync<object[]>(ct);
        int Count(string kind) => (int)(long)(rows.FirstOrDefault(row => (string)row[0] == kind)?[1] ?? 0L);
        return (Count("missing"), Count("stale"), Count("orphaned"));
    }

    // ---- verification --------------------------------------------------------------------------------------------------

    /// <summary>Titles of up to <paramref name="take"/> random eligible indexed pages at their current published revision (no templates).</summary>
    public async Task<IReadOnlyList<(Guid PageId, string Title)>> SampleTitlesAsync(Guid generationId, int take, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($"""
            WITH {Eligible}
            SELECT k."PageId",r."Title" FROM "KnowledgePage" k JOIN "PageRevision" r ON r."Id"=k."RevisionId"
            JOIN eligible p ON p."Id"=k."PageId" AND p."PublishedRevisionId"=k."RevisionId"
            WHERE k."GenerationId"=:g AND NOT k."IsTemplate" AND NOT p."IsTemplate" ORDER BY random() LIMIT :take
            """)
            .AddScalar("PageId", NHibernateUtil.Guid).AddScalar("Title", NHibernateUtil.String)
            .SetParameter("g", generationId).SetParameter("take", take).ListAsync<object[]>(ct);
        return rows.Select(row => ((Guid)row[0], (string)row[1])).ToArray();
    }

    /// <summary>The five pages of the generation nearest to the vector by their best chunk (HNSW with an iterative scan, re-sorted).</summary>
    public async Task<IReadOnlyList<Guid>> NearestPagesAsync(Guid generationId, string vector, CancellationToken ct)
    {
        await session.CreateSQLQuery("SET LOCAL hnsw.iterative_scan = relaxed_order").ExecuteUpdateAsync(ct);
        return (await session.CreateSQLQuery("""
            WITH near AS MATERIALIZED (
              SELECT c."PageId",c."Embedding" <=> CAST(:v AS vector) AS d FROM "KnowledgeChunk" c
              WHERE c."GenerationId"=:g ORDER BY c."Embedding" <=> CAST(:v AS vector) LIMIT 50)
            SELECT "PageId" FROM near GROUP BY "PageId" ORDER BY min(d),"PageId" LIMIT 5
            """)
            .AddScalar("PageId", NHibernateUtil.Guid)
            .SetParameter("g", generationId).SetParameter("v", vector, NHibernateUtil.StringClob).ListAsync<Guid>(ct)).ToArray();
    }

    // ---- worker: claim -------------------------------------------------------------------------------------------------

    /// <summary>Leases up to <paramref name="max"/> due tasks of the given generations, active generation first, skipping rows
    /// another worker holds. Returns their tokens.</summary>
    /// <remarks>A task without a counted attempt is due at once. The database enqueued it at its own <c>now()</c>, which must
    /// not be compared with the worker's clock: a worker clock behind the database's would hold new work back by the skew.
    /// Only a backoff, which the worker set from its own clock, waits for <paramref name="now"/>.</remarks>
    public async Task<IReadOnlyList<(Guid GenerationId, Guid PageId, DateTime Token)>> ClaimAsync(IReadOnlyCollection<Guid> generations, int max,
        DateTime now, DateTime leasedUntil, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
            WITH due AS (
              SELECT t."GenerationId",t."PageId" FROM "IndexTask" t JOIN "IndexGeneration" g ON g."Id"=t."GenerationId"
              WHERE t."GenerationId" IN (:gens) AND (t."Attempts"=0 OR t."NextAttemptAt"<=:now) AND (t."LeasedUntil" IS NULL OR t."LeasedUntil"<:now)
              ORDER BY g."State"='Active' DESC,t."NextAttemptAt",t."EnqueuedAt"
              LIMIT :max FOR UPDATE OF t SKIP LOCKED)
            UPDATE "IndexTask" t SET "LeasedUntil"=:lease FROM due d WHERE t."GenerationId"=d."GenerationId" AND t."PageId"=d."PageId"
            RETURNING t."GenerationId",t."PageId",t."EnqueuedAt"
            """)
            .AddScalar("GenerationId", NHibernateUtil.Guid).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("EnqueuedAt", NHibernateUtil.UtcDateTime)
            .SetParameterList("gens", generations).SetParameter("now", now, NHibernateUtil.UtcDateTime)
            .SetParameter("lease", leasedUntil, NHibernateUtil.UtcDateTime).SetParameter("max", max)
            .ListAsync<object[]>(ct);
        return rows.Select(row => ((Guid)row[0], (Guid)row[1], (DateTime)row[2])).ToArray();
    }

    /// <summary>When each generation's next task becomes claimable by <see cref="ClaimAsync"/>'s own condition: one without a counted
    /// attempt at once, else at its backoff, and a leased one when its lease ends. No row for a generation without tasks.</summary>
    public async Task<IReadOnlyList<(Guid GenerationId, DateTime At)>> NextDueAsync(IReadOnlyCollection<Guid> generations, DateTime now, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
            SELECT t."GenerationId",min(GREATEST(CASE WHEN t."Attempts"=0 THEN :now ELSE t."NextAttemptAt" END,COALESCE(t."LeasedUntil",:now))) AS at
            FROM "IndexTask" t WHERE t."GenerationId" IN (:gens) GROUP BY t."GenerationId"
            """)
            .AddScalar("GenerationId", NHibernateUtil.Guid).AddScalar("at", NHibernateUtil.UtcDateTime)
            .SetParameterList("gens", generations).SetParameter("now", now, NHibernateUtil.UtcDateTime)
            .ListAsync<object[]>(ct);
        return rows.Select(row => ((Guid)row[0], (DateTime)row[1])).ToArray();
    }

    /// <summary>The current state of the pages. Content (title, the text for AI, headings) is read only for eligible pages, and for
    /// templates only the title: an excluded space or a draft never leaves the database.</summary>
    public async Task<IReadOnlyDictionary<Guid, PageSnapshot>> PagesAsync(IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery(Search.SearchDocuments.NoJit + $"""
            WITH pages AS MATERIALIZED (
              SELECT p."Id",p."SpaceId",p."PublishedRevisionId",p."IsTemplate",{Candidate("p")} AS candidate
              FROM "Page" p WHERE p."Id" IN (:ids)),
            spaces AS MATERIALIZED (SELECT x."SpaceId",{AccessSql.SpaceInAi("x.\"SpaceId\"")} AS included FROM (SELECT DISTINCT "SpaceId" FROM pages) x),
            state AS MATERIALIZED (
              SELECT p."Id",p."PublishedRevisionId",p."IsTemplate",p.candidate AND s.included AS eligible
              FROM pages p JOIN spaces s ON s."SpaceId"=p."SpaceId")
            SELECT st."Id",st."PublishedRevisionId",st."IsTemplate",st.eligible,
              CASE WHEN st.eligible THEN r."Title" END AS title,
              CASE WHEN st.eligible AND NOT st."IsTemplate" THEN r."ContentJson" END AS content,
              CASE WHEN st.eligible AND NOT st."IsTemplate"
                THEN CAST({SearchSqlText.Headings(SearchSqlText.StripSecrets("r.\"Markdown\""), "r.\"ContentJson\"")} AS text) END AS headings
            FROM state st
            LEFT JOIN "PageRevision" r ON st.eligible AND r."Id"=st."PublishedRevisionId"
            """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("PublishedRevisionId", NHibernateUtil.Guid).AddScalar("IsTemplate", NHibernateUtil.Boolean)
            .AddScalar("eligible", NHibernateUtil.Boolean).AddScalar("title", NHibernateUtil.String)
            .AddScalar("content", NHibernateUtil.StringClob).AddScalar("headings", NHibernateUtil.StringClob)
            .SetParameterList("ids", pageIds).ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => new PageSnapshot((Guid?)row[1], (bool)row[2], (bool)row[3], (string?)row[4],
            row[5] is string content ? OutboundDocument.AiText(content) : null, ParseHeadings((string?)row[6])));
    }

    private static IReadOnlyList<IndexHeading> ParseHeadings(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(item => new IndexHeading(item.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                item.TryGetProperty("headingId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null))
            .ToArray();
    }

    /// <summary>What the generations hold for the pages: revision, template state, title hash, and the hashes of their chunks.</summary>
    public async Task<IReadOnlyDictionary<(Guid GenerationId, Guid PageId), IndexedPageState>> IndexedAsync(
        IReadOnlyCollection<(Guid GenerationId, Guid PageId)> pairs, CancellationToken ct)
    {
        if (pairs.Count == 0)
        {
            return new Dictionary<(Guid, Guid), IndexedPageState>();
        }
        var keys = string.Join(",", pairs.Select((_, i) => $"(:g{i},:p{i})"));
        var pagesQuery = session.CreateSQLQuery($"""
            SELECT k."GenerationId",k."PageId",k."RevisionId",k."IsTemplate",k."TitleHash" FROM "KnowledgePage" k
            WHERE (k."GenerationId",k."PageId") IN ({keys})
            """)
            .AddScalar("GenerationId", NHibernateUtil.Guid).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("RevisionId", NHibernateUtil.Guid)
            .AddScalar("IsTemplate", NHibernateUtil.Boolean).AddScalar("TitleHash", NHibernateUtil.BinaryBlob);
        var chunksQuery = session.CreateSQLQuery($"""
            SELECT c."GenerationId",c."PageId",c."TextHash" FROM "KnowledgeChunk" c WHERE (c."GenerationId",c."PageId") IN ({keys})
            """)
            .AddScalar("GenerationId", NHibernateUtil.Guid).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("TextHash", NHibernateUtil.BinaryBlob);
        var i = 0;
        foreach (var (generation, page) in pairs)
        {
            pagesQuery.SetParameter("g" + i, generation).SetParameter("p" + i, page);
            chunksQuery.SetParameter("g" + i, generation).SetParameter("p" + i, page);
            i++;
        }
        var hashes = (await chunksQuery.ListAsync<object[]>(ct))
            .ToLookup(row => ((Guid)row[0], (Guid)row[1]), row => VectorText.Hex((byte[])row[2]));
        return (await pagesQuery.ListAsync<object[]>(ct)).ToDictionary(row => ((Guid)row[0], (Guid)row[1]),
            row => new IndexedPageState((Guid)row[2], (bool)row[3], VectorText.Hex((byte[])row[4]),
                hashes[((Guid)row[0], (Guid)row[1])].ToHashSet(StringComparer.Ordinal)));
    }

    /// <summary>The page no longer belongs in the generation: its row and chunks go, and so does the task. Its neighbour rows go
    /// by cascade, under the generation's neighbour lock, so a running recompute never references the deleted row.</summary>
    public async Task RemoveAsync(Guid generationId, Guid pageId, CancellationToken ct)
    {
        await LockNeighborsAsync(generationId, ct);
        await session.CreateSQLQuery("""
            DELETE FROM "KnowledgePage" WHERE "GenerationId"=:g AND "PageId"=:p;
            DELETE FROM "IndexTask" WHERE "GenerationId"=:g AND "PageId"=:p;
            """).SetParameter("g", generationId).SetParameter("p", pageId).ExecuteUpdateAsync(ct);
    }

    public Task DeleteTaskAsync(Guid generationId, Guid pageId, CancellationToken ct) => session.CreateSQLQuery("""
            DELETE FROM "IndexTask" WHERE "GenerationId"=:g AND "PageId"=:p
            """).SetParameter("g", generationId).SetParameter("p", pageId).ExecuteUpdateAsync(ct);

    // ---- worker: write -------------------------------------------------------------------------------------------------

    /// <summary>FOR SHARE on the page, then its state. Taken before the task row: a publication holds the page and enqueues at its
    /// commit, so this order never waits in a cycle with it.</summary>
    public async Task<PageSnapshot?> LockPageAsync(Guid pageId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($"""
            SELECT p."PublishedRevisionId",p."IsTemplate",{EligibleRow("p")} AS eligible FROM "Page" p WHERE p."Id"=:p FOR SHARE
            """)
            .AddScalar("PublishedRevisionId", NHibernateUtil.Guid).AddScalar("IsTemplate", NHibernateUtil.Boolean).AddScalar("eligible", NHibernateUtil.Boolean)
            .SetParameter("p", pageId).ListAsync<object[]>(ct);
        return rows.Count == 0 ? null : new PageSnapshot((Guid?)rows[0][0], (bool)rows[0][1], (bool)rows[0][2], null, null, []);
    }

    /// <summary>FOR UPDATE on the task: its token (EnqueuedAt) and attempts, or null when it is gone.</summary>
    public async Task<(DateTime Token, int Attempts)?> LockTaskAsync(Guid generationId, Guid pageId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
            SELECT t."EnqueuedAt",t."Attempts" FROM "IndexTask" t JOIN "IndexGeneration" g ON g."Id"=t."GenerationId"
            WHERE t."GenerationId"=:g AND t."PageId"=:p AND g."State" IN ('Building','Active') FOR UPDATE OF t
            """)
            .AddScalar("EnqueuedAt", NHibernateUtil.UtcDateTime).AddScalar("Attempts", NHibernateUtil.Int32)
            .SetParameter("g", generationId).SetParameter("p", pageId).ListAsync<object[]>(ct);
        return rows.Count == 0 ? null : ((DateTime)rows[0][0], (int)rows[0][1]);
    }

    /// <summary>Ends the lease. With <paramref name="next"/> the attempt counts (a failure that is not an outage).</summary>
    public Task ReleaseAsync(Guid generationId, Guid pageId, string? code, DateTime? next, CancellationToken ct) => session.CreateSQLQuery("""
            UPDATE "IndexTask" SET "LeasedUntil"=NULL,"LastErrorCode"=:code,
              "Attempts"=CASE WHEN :counted THEN "Attempts"+1 ELSE "Attempts" END,
              "NextAttemptAt"=COALESCE(:next,"NextAttemptAt")
            WHERE "GenerationId"=:g AND "PageId"=:p
            """)
        .SetParameter("g", generationId).SetParameter("p", pageId).SetParameter("code", code, NHibernateUtil.String)
        .SetParameter("counted", next is not null).SetParameter("next", next, NHibernateUtil.UtcDateTime)
        .ExecuteUpdateAsync(ct);

    public Task ReleaseLeaseAsync(Guid generationId, Guid pageId, CancellationToken ct) => session.CreateSQLQuery("""
            UPDATE "IndexTask" SET "LeasedUntil"=NULL WHERE "GenerationId"=:g AND "PageId"=:p
            """).SetParameter("g", generationId).SetParameter("p", pageId).ExecuteUpdateAsync(ct);

    /// <summary>The vectors the generation holds for the page under the given hashes (title and chunks), as text.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ReusableAsync(Guid generationId, Guid pageId, IReadOnlyCollection<byte[]> hashes, CancellationToken ct)
    {
        if (hashes.Count == 0)
        {
            return new Dictionary<string, string>();
        }
        var rows = await session.CreateSQLQuery("""
            SELECT c."TextHash" AS h,CAST(c."Embedding" AS text) AS v FROM "KnowledgeChunk" c
            WHERE c."GenerationId"=:g AND c."PageId"=:p AND c."TextHash" IN (:hashes)
            UNION ALL
            SELECT k."TitleHash",CAST(k."TitleEmbedding" AS text) FROM "KnowledgePage" k
            WHERE k."GenerationId"=:g AND k."PageId"=:p AND k."TitleHash" IN (:hashes)
            """)
            .AddScalar("h", NHibernateUtil.BinaryBlob).AddScalar("v", NHibernateUtil.StringClob)
            .SetParameter("g", generationId).SetParameter("p", pageId).SetParameterList("hashes", hashes, NHibernateUtil.BinaryBlob)
            .ListAsync<object[]>(ct);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result.TryAdd(VectorText.Hex((byte[])row[0]), (string)row[1]);
        }
        return result;
    }

    /// <summary>Replaces the page's row and chunks in the generation; the centroid is the normalized mean of the chunk vectors
    /// (none for a template).</summary>
    public async Task WriteAsync(Guid generationId, Guid pageId, Guid revisionId, bool isTemplate, string titleVector, byte[] titleHash,
        IReadOnlyList<(IndexedChunk Chunk, string Vector)> chunks, CancellationToken ct)
    {
        await session.CreateSQLQuery("""
            INSERT INTO "KnowledgePage"("GenerationId","PageId","RevisionId","IsTemplate","TitleEmbedding","TitleHash","Centroid","IndexedAt")
            VALUES (:g,:p,:r,:template,CAST(:title AS vector),:hash,NULL,clock_timestamp())
            ON CONFLICT ("GenerationId","PageId") DO UPDATE SET "RevisionId"=EXCLUDED."RevisionId","IsTemplate"=EXCLUDED."IsTemplate",
              "TitleEmbedding"=EXCLUDED."TitleEmbedding","TitleHash"=EXCLUDED."TitleHash","Centroid"=NULL,"IndexedAt"=EXCLUDED."IndexedAt";
            DELETE FROM "KnowledgeChunk" WHERE "GenerationId"=:g AND "PageId"=:p;
            """)
            .SetParameter("g", generationId).SetParameter("p", pageId).SetParameter("r", revisionId).SetParameter("template", isTemplate)
            .SetParameter("title", titleVector, NHibernateUtil.StringClob).SetParameter("hash", titleHash, NHibernateUtil.BinaryBlob)
            .ExecuteUpdateAsync(ct);
        // 7 parameters a row: 500 rows stay far below PostgreSQL's 65 535.
        foreach (var batch in chunks.Chunk(500))
        {
            var values = string.Join(",", batch.Select((_, i) => $"(:g,:p,:r,:o{i},:hid{i},:ht{i},:t{i},:h{i},CAST(:v{i} AS vector))"));
            var insert = session.CreateSQLQuery($"""
                INSERT INTO "KnowledgeChunk"("GenerationId","PageId","RevisionId","Ordinal","HeadingId","HeadingTitle","Text","TextHash","Embedding")
                VALUES {values}
                """).SetParameter("g", generationId).SetParameter("p", pageId).SetParameter("r", revisionId);
            for (var i = 0; i < batch.Length; i++)
            {
                var (chunk, vector) = batch[i];
                insert.SetParameter("o" + i, (short)chunk.Ordinal, NHibernateUtil.Int16)
                    .SetParameter("hid" + i, chunk.HeadingId, NHibernateUtil.String)
                    .SetParameter("ht" + i, chunk.HeadingTitle, NHibernateUtil.String)
                    .SetParameter("t" + i, chunk.Text, NHibernateUtil.StringClob)
                    .SetParameter("h" + i, chunk.TextHash, NHibernateUtil.BinaryBlob)
                    .SetParameter("v" + i, vector, NHibernateUtil.StringClob);
            }
            await insert.ExecuteUpdateAsync(ct);
        }
        if (chunks.Count > 0)
        {
            await session.CreateSQLQuery("""
                UPDATE "KnowledgePage" k SET "Centroid"=(SELECT l2_normalize(avg(c."Embedding")) FROM "KnowledgeChunk" c
                  WHERE c."GenerationId"=:g AND c."PageId"=:p)
                WHERE k."GenerationId"=:g AND k."PageId"=:p
                """).SetParameter("g", generationId).SetParameter("p", pageId).ExecuteUpdateAsync(ct);
        }
    }

    // ---- related pages (V4) --------------------------------------------------------------------------------------------

    /// <summary>One writer of a generation's lists at a time (the worker's store, the maintenance batches; several instances too). The text
    /// key follows IAtlasLocks.AcquireAdvisoryAsync(string).</summary>
    private Task LockNeighborsAsync(Guid generationId, CancellationToken ct) => session.CreateSQLQuery("""
            SELECT pg_advisory_xact_lock(hashtextextended('page-neighbors:' || CAST(:g AS text), 812))
            """).SetParameter("g", generationId).ExecuteUpdateAsync(ct);

    /// <summary>The pages' own 30 nearest pages of the generation by centroid (no templates, not themselves), ranked by distance. HNSW with an
    /// iterative scan (the generation and template filters), re-sorted outside the scan.</summary>
    private async Task InsertOwnNeighborsAsync(Guid generationId, IReadOnlyCollection<Guid> pages, CancellationToken ct)
    {
        await session.CreateSQLQuery("""SELECT set_config('hnsw.iterative_scan', 'relaxed_order', true)""").ExecuteUpdateAsync(ct);
        await session.CreateSQLQuery("""
            INSERT INTO "PageNeighbor"("GenerationId","PageId","NeighborPageId","Rank","Similarity")
            SELECT :g, src."PageId", near."PageId",
              CAST(row_number() OVER (PARTITION BY src."PageId" ORDER BY near.d, near."PageId") AS smallint), CAST(1 - near.d AS real)
            FROM "KnowledgePage" src
            CROSS JOIN LATERAL (
              SELECT k."PageId", k."Centroid" <=> src."Centroid" AS d FROM "KnowledgePage" k
              WHERE k."GenerationId" = :g AND k."PageId" <> src."PageId" AND NOT k."IsTemplate" AND k."Centroid" IS NOT NULL
              ORDER BY k."Centroid" <=> src."Centroid" LIMIT 30) near
            WHERE src."GenerationId" = :g AND src."PageId" IN (:pages) AND NOT src."IsTemplate" AND src."Centroid" IS NOT NULL
            """).SetParameter("g", generationId).SetParameterList("pages", pages.ToList()).ExecuteUpdateAsync(ct);
    }

    /// <summary>
    /// R1 after a write: the page's own list is recomputed, it leaves every other list, and it joins the lists of its own neighbours where it
    /// beats their weakest entry or fills a list shorter than 30 (similarity is symmetric). Lists of pages farther away keep their entries
    /// until the maintenance recomputes the generation; a list that lost the page may hold 29 for that long. A template or a page without
    /// chunks ends with no list and in none. Under the generation's neighbour lock.
    /// </summary>
    public async Task RefreshNeighborsAsync(Guid generationId, Guid pageId, CancellationToken ct)
    {
        await LockNeighborsAsync(generationId, ct);
        await session.CreateSQLQuery("""
            DELETE FROM "PageNeighbor" n WHERE n."GenerationId"=:g AND (n."PageId"=:p OR n."NeighborPageId"=:p)
            """).SetParameter("g", generationId).SetParameter("p", pageId).ExecuteUpdateAsync(ct);
        await InsertOwnNeighborsAsync(generationId, [pageId], ct);
        await session.CreateSQLQuery("""
            INSERT INTO "PageNeighbor"("GenerationId","PageId","NeighborPageId","Rank","Similarity")
            SELECT :g, own."NeighborPageId", :p, 30, own."Similarity"
            FROM "PageNeighbor" own
            LEFT JOIN LATERAL (
              SELECT count(*) AS n, min(o."Similarity") AS weakest FROM "PageNeighbor" o
              WHERE o."GenerationId" = :g AND o."PageId" = own."NeighborPageId") other ON true
            WHERE own."GenerationId" = :g AND own."PageId" = :p AND (other.n < 30 OR own."Similarity" > other.weakest)
            """).SetParameter("g", generationId).SetParameter("p", pageId).ExecuteUpdateAsync(ct);
        // The lists it joined: keep the best 30, renumbered by similarity.
        await session.CreateSQLQuery("""
            WITH ranked AS (
              SELECT o."PageId", o."NeighborPageId", o."Rank",
                row_number() OVER (PARTITION BY o."PageId" ORDER BY o."Similarity" DESC, o."NeighborPageId") AS r
              FROM "PageNeighbor" o
              WHERE o."GenerationId" = :g
                AND o."PageId" IN (SELECT x."PageId" FROM "PageNeighbor" x WHERE x."GenerationId" = :g AND x."NeighborPageId" = :p)),
            dropped AS (
              DELETE FROM "PageNeighbor" o USING ranked x
              WHERE o."GenerationId" = :g AND o."PageId" = x."PageId" AND o."NeighborPageId" = x."NeighborPageId" AND x.r > 30)
            UPDATE "PageNeighbor" o SET "Rank" = CAST(x.r AS smallint) FROM ranked x
            WHERE o."GenerationId" = :g AND o."PageId" = x."PageId" AND o."NeighborPageId" = x."NeighborPageId"
              AND x.r <= 30 AND x."Rank" <> x.r
            """).SetParameter("g", generationId).SetParameter("p", pageId).ExecuteUpdateAsync(ct);
    }

    /// <summary>
    /// Live generations whose neighbour lists the maintenance recomputes now: never recomputed or not since the generation was
    /// activated (activation clears the refresh time), or more than <paramref name="maxAge"/> ago. A building generation waits until no task of its boundary is
    /// left, so its lists are computed once over the complete build.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> NeighborRefreshesDueAsync(DateTime now, TimeSpan maxAge, CancellationToken ct)
        => (await session.CreateSQLQuery($"""
            SELECT g."Id" FROM "IndexGeneration" g
            WHERE g."State" IN {Live}
              AND (g."NeighborsRefreshedAt" IS NULL OR g."NeighborsRefreshedAt"<:stale)
              AND NOT EXISTS (SELECT 1 FROM "IndexTask" t WHERE t."GenerationId"=g."Id" AND t."EnqueuedAt"<=g."BoundaryAt")
            ORDER BY g."State"='Active' DESC,g."CreatedAt"
            """)
            .AddScalar("Id", NHibernateUtil.Guid)
            .SetParameter("stale", now - maxAge, NHibernateUtil.UtcDateTime).ListAsync<Guid>(ct)).ToArray();

    /// <summary>One batch of a generation's full recompute: the own lists of up to <paramref name="batch"/> pages of the generation after
    /// <paramref name="after"/> (by Id). Returns the last page of the batch, or null when the generation is done, which then records the refresh
    /// as <paramref name="started"/>. Once every page's own list is recomputed, every list is exact; a batch is one short transaction.</summary>
    public async Task<Guid?> RefreshGenerationNeighborsAsync(Guid generationId, Guid? after, int batch, DateTime started, CancellationToken ct)
    {
        await LockNeighborsAsync(generationId, ct);
        var pages = await session.CreateSQLQuery("""
            SELECT k."PageId" FROM "KnowledgePage" k
            WHERE k."GenerationId"=:g AND (CAST(:after AS uuid) IS NULL OR k."PageId" > CAST(:after AS uuid))
            ORDER BY k."PageId" LIMIT :batch
            """)
            .AddScalar("PageId", NHibernateUtil.Guid)
            .SetParameter("g", generationId).SetParameter("after", after, NHibernateUtil.Guid).SetParameter("batch", batch)
            .ListAsync<Guid>(ct);
        if (pages.Count == 0)
        {
            await session.CreateSQLQuery("""UPDATE "IndexGeneration" SET "NeighborsRefreshedAt"=:started WHERE "Id"=:g""")
                .SetParameter("started", started, NHibernateUtil.UtcDateTime).SetParameter("g", generationId).ExecuteUpdateAsync(ct);
            return null;
        }
        await session.CreateSQLQuery("""DELETE FROM "PageNeighbor" n WHERE n."GenerationId"=:g AND n."PageId" IN (:pages)""")
            .SetParameter("g", generationId).SetParameterList("pages", pages.ToList()).ExecuteUpdateAsync(ct);
        await InsertOwnNeighborsAsync(generationId, pages.ToList(), ct);
        return pages[^1];
    }
}

public sealed record GenerationStats(int EligiblePages, int IndexedPages, int Chunks, int PendingTasks, int FailingTasks);

/// <summary>A page as the index sees it now; content only for an eligible page (Markdown and headings only for a non-template).</summary>
public sealed record PageSnapshot(Guid? PublishedRevisionId, bool IsTemplate, bool Eligible, string? Title, string? Markdown, IReadOnlyList<IndexHeading> Headings);

public sealed record IndexedPageState(Guid RevisionId, bool IsTemplate, string TitleHash, IReadOnlySet<string> ChunkHashes);
