using NHibernate;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Search;

/// <summary>
/// The search documents of pages (Atlas 3.6, no logic in the database): <c>WorkingSearchDocument</c>, the working revision a writer
/// searches right after a save, and <c>SearchDocument</c>, the published revision with its headings and aliases (the titles it was
/// published under, the source paths of its published imports). The code that changes their source rewrites them in its own
/// transaction, as BoardSearch does for boards; the triggers of migrations 002–2026110860 did it before migration 2026110935. Each
/// refresh is one statement of built-in functions (<see cref="SearchSqlText"/>) that rebuilds the documents of the given pages from
/// their current rows, so it is idempotent and serves the rebuild job too (<see cref="SearchDocumentsRebuildJob"/>).
/// SearchDocumentCoverageTests keeps the list of writers whole.
/// </summary>
public static class SearchDocuments
{
    /// <summary>After a revision of the pages was written, closed or changed in place: their working documents.</summary>
    public static Task RefreshWorkingAsync(ISession session, IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
        => ExecuteAsync(session, RefreshWorkingSql, pageIds, ct);

    /// <summary>After a publication, a move to another space, a change of what the aliases read (publications, imports) or a published
    /// revision changed in place: their published documents.</summary>
    public static Task RefreshPublishedAsync(ISession session, IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
        => ExecuteAsync(session, RefreshPublishedSql, pageIds, ct);

    /// <summary>Both documents of the pages: a revision rewritten in place may be the working and the published one.</summary>
    public static Task RefreshAsync(ISession session, IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
        => ExecuteAsync(session, RefreshWorkingSql + ";\n" + RefreshPublishedSql, pageIds, ct);

    /// <summary>Both documents of every page, as one batch of statements (a restore's rebuild, InstallationModules.PagesRebuild).</summary>
    public static readonly string RebuildAllSql = NoJit + WorkingSql("IN (SELECT \"Id\" FROM \"Page\")") + ";\n" + PublishedSql("IN (SELECT \"Id\" FROM \"Page\")");

    /// <summary>Before a statement of <see cref="SearchSqlText"/> in the same query text: JIT off for the rest of the transaction. No
    /// result set, so a read that starts with it still reads its own rows first.</summary>
    public const string NoJit = "SET LOCAL jit = off;\n";

    private static readonly string RefreshWorkingSql = WorkingSql("= " + GuidList.Any(":pages"));
    private static readonly string RefreshPublishedSql = PublishedSql("= " + GuidList.Any(":pages"));

    private static async Task ExecuteAsync(ISession session, string sql, IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        if (pageIds.Count == 0)
        {
            return;
        }
        // The statement reads the rows the session holds. JIT is off for the rest of the transaction (SearchSqlText).
        await session.FlushAsync(ct);
        await session.CreateSQLQuery(NoJit + sql).SetParameter("pages", GuidList.Param(pageIds), NHibernateUtil.String).ExecuteUpdateAsync(ct);
    }

    /// <summary>
    /// The working document of each page from its open revision; a page without one has none. The pages that lost their open revision
    /// and the ones that have one are disjoint, so the delete and the upsert never touch the same row.
    /// </summary>
    private static string WorkingSql(string pages) => $$"""
        WITH gone AS (
          DELETE FROM "WorkingSearchDocument" w WHERE w."PageId" {{pages}}
            AND NOT EXISTS (SELECT 1 FROM "PageRevision" o WHERE o."PageId" = w."PageId" AND o."ActiveTo" IS NULL))
        INSERT INTO "WorkingSearchDocument"("PageId","RevisionId","NormalizedTitle","NormalizedBody","Vector","HeadingVector","Headings","ChangedAt")
        SELECT r."PageId",r."Id",n.title,n.body,
          {{SearchSqlText.Vector("r.\"Title\"", "n.title", "hv.vector", "src.body", "n.body")}},
          hv.vector,hs.headings,r."ActiveFrom"
        FROM "PageRevision" r
        CROSS JOIN LATERAL (SELECT {{SearchSqlText.Body("r.\"Markdown\"", "r.\"ContentJson\"")}} AS body OFFSET 0) src
        CROSS JOIN LATERAL (SELECT {{SearchSqlText.Normalize("r.\"Title\"")}} AS title,{{SearchSqlText.Normalize("src.body")}} AS body OFFSET 0) n
        CROSS JOIN LATERAL (SELECT {{SearchSqlText.Headings(SearchSqlText.StripSecrets("r.\"Markdown\""), "r.\"ContentJson\"")}} AS headings OFFSET 0) hs
        CROSS JOIN LATERAL (SELECT {{SearchSqlText.HeadingVector("hs.headings")}} AS vector OFFSET 0) hv
        WHERE r."PageId" {{pages}} AND r."ActiveTo" IS NULL
        ON CONFLICT ("PageId") DO UPDATE SET "RevisionId"=EXCLUDED."RevisionId","NormalizedTitle"=EXCLUDED."NormalizedTitle",
          "NormalizedBody"=EXCLUDED."NormalizedBody","Vector"=EXCLUDED."Vector","HeadingVector"=EXCLUDED."HeadingVector",
          "Headings"=EXCLUDED."Headings","ChangedAt"=EXCLUDED."ChangedAt"
        """;

    /// <summary>
    /// The published document of each page from its published revision; a page without one has none. Aliases: the titles of its last
    /// 32 publications, newest first, other than its title, then the source paths (by path) of at most 16 imports of it of which an
    /// exact checkpoint was published; each text once, its lexemes after a gap of their own.
    /// </summary>
    private static string PublishedSql(string pages) => $$"""
        WITH gone AS (
          DELETE FROM "SearchDocument" d WHERE d."PageId" {{pages}}
            AND NOT EXISTS (SELECT 1 FROM "Page" o WHERE o."Id" = d."PageId" AND o."PublishedRevisionId" IS NOT NULL))
        INSERT INTO "SearchDocument"("PageId","SpaceId","RevisionId","Title","Body","NormalizedTitle","NormalizedBody","Vector","Headings",
          "HeadingVector","PublishedAt","Aliases","AliasVector")
        SELECT p."Id",p."SpaceId",r."Id",r."Title",src.body,n.title,n.body,
          {{SearchSqlText.Vector("r.\"Title\"", "n.title", "hv.vector", "src.body", "n.body")}},
          hs.headings,hv.vector,
          COALESCE((SELECT v."ActiveFrom" FROM "PagePublication" v WHERE v."PageId"=p."Id" AND v."ActiveTo" IS NULL),r."ActiveFrom"),
          al.aliases,al.vector
        FROM "Page" p JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
        CROSS JOIN LATERAL (SELECT {{SearchSqlText.Body("r.\"Markdown\"", "r.\"ContentJson\"")}} AS body OFFSET 0) src
        CROSS JOIN LATERAL (SELECT {{SearchSqlText.Normalize("r.\"Title\"")}} AS title,{{SearchSqlText.Normalize("src.body")}} AS body OFFSET 0) n
        CROSS JOIN LATERAL (SELECT {{SearchSqlText.Headings(SearchSqlText.StripSecrets("r.\"Markdown\""), "r.\"ContentJson\"")}} AS headings OFFSET 0) hs
        CROSS JOIN LATERAL (SELECT {{SearchSqlText.HeadingVector("hs.headings")}} AS vector OFFSET 0) hv
        CROSS JOIN LATERAL (
          WITH RECURSIVE candidate AS (
            SELECT c.ord,c.text,{{SearchSqlText.Normalize("c.text")}} AS normalized,c.titled
            FROM (
              SELECT row_number() OVER (ORDER BY publication."ActiveFrom" DESC) AS ord,t."Title" AS text,true AS titled
              FROM (SELECT x."RevisionId",x."ActiveFrom" FROM "PagePublication" x WHERE x."PageId"=p."Id" ORDER BY x."ActiveFrom" DESC LIMIT 32) publication
              JOIN "PageRevision" t ON t."Id"=publication."RevisionId"
              UNION ALL
              SELECT 100+row_number() OVER (ORDER BY m."SourcePath",m."Id"),m."SourcePath",false
              FROM (SELECT y."SourcePath",y."Id" FROM "WikiImportPage" y WHERE y."PageId"=p."Id" AND EXISTS (
                  SELECT 1 FROM "WikiImportCheckpoint" k JOIN "PagePublication" z ON z."PageId"=y."PageId" AND z."RevisionId"=k."RevisionId"
                  WHERE k."ImportPageId"=y."Id")
                ORDER BY y."SourcePath",y."Id" LIMIT 16) m
            ) c
          ), accepted AS (
            SELECT row_number() OVER (ORDER BY first.ord) AS ord,first.text,first.normalized FROM (
              SELECT DISTINCT ON (a.text) a.ord,a.text,a.normalized FROM candidate a
              WHERE a.normalized<>'' AND (NOT a.titled OR a.normalized<>n.title)
              ORDER BY a.text,a.ord) first
          ), joined(ord,vector) AS (
            SELECT CAST(0 AS bigint),CAST('' AS tsvector)
            UNION ALL
            SELECT a.ord,j.vector || CAST('''__scaleatlas_alias_boundary__'':1' AS tsvector) || {{SearchSqlText.Lexemes("a.text", "a.normalized")}}
            FROM joined j JOIN accepted a ON a.ord=j.ord+1
          )
          SELECT COALESCE((SELECT jsonb_agg(to_jsonb(a.text) ORDER BY a.ord) FROM accepted a),CAST('[]' AS jsonb)) AS aliases,
            (SELECT ts_delete(j.vector,'__scaleatlas_alias_boundary__') FROM joined j ORDER BY j.ord DESC LIMIT 1) AS vector
        ) al
        WHERE p."Id" {{pages}}
        ON CONFLICT ("PageId") DO UPDATE SET "SpaceId"=EXCLUDED."SpaceId","RevisionId"=EXCLUDED."RevisionId","Title"=EXCLUDED."Title",
          "Body"=EXCLUDED."Body","NormalizedTitle"=EXCLUDED."NormalizedTitle","NormalizedBody"=EXCLUDED."NormalizedBody","Vector"=EXCLUDED."Vector",
          "Headings"=EXCLUDED."Headings","HeadingVector"=EXCLUDED."HeadingVector","PublishedAt"=EXCLUDED."PublishedAt",
          "Aliases"=EXCLUDED."Aliases","AliasVector"=EXCLUDED."AliasVector"
        """;
}

/// <summary>
/// The search documents of every page rebuilt from their current revisions, in checkpointed batches of pages by id: by hand from Úlohy,
/// or after bare SQL that wrote pages. Migration 2026110935 needs no run (the code computes the rows the triggers wrote) and a restore
/// rebuilds in its own transaction (InstallationModules.PagesRebuild). Idempotent; a second run rewrites the same rows.
/// </summary>
public sealed class SearchDocumentsRebuildJob : IJobDefinition
{
    public const string JobId = "search.documents-rebuild";
    private const int Batch = 50;

    public string Id => JobId;

    public string Title => "Přestavba vyhledávání stránek";

    public string Description => "Znovu sestaví vyhledávací dokumenty všech stránek (pracovní i publikované verze, nadpisy a dřívější názvy) z jejich aktuálního obsahu.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var progress = context.Progress<RebuildProgress>() ?? new RebuildProgress(null, 0);
        while (true)
        {
            var next = await context.StepAsync((session, token) => RunBatchAsync(session, progress, token), ct);
            context.Summarize(new { pages = next.Pages });
            if (next.Pages == progress.Pages)
            {
                break;
            }
            progress = next;
        }
        context.Log.LogInformation("Rebuilt the search documents of {Pages} pages", progress.Pages);
    }

    /// <summary>The next batch after the checkpoint: its pages' documents and the new checkpoint, committed together.</summary>
    public static async Task<RebuildProgress> RunBatchAsync(ISession session, RebuildProgress progress, CancellationToken ct)
    {
        var pages = await session.CreateSQLQuery("""
                SELECT "Id" AS id FROM "Page" WHERE (CAST(:after AS uuid) IS NULL OR "Id" > CAST(:after AS uuid)) ORDER BY "Id" LIMIT :batch
                """)
            .AddScalar("id", NHibernateUtil.Guid).SetParameter("after", progress.After?.ToString(), NHibernateUtil.String).SetParameter("batch", Batch)
            .ListAsync<Guid>(ct);
        await SearchDocuments.RefreshAsync(session, pages.ToArray(), ct);
        return pages.Count == 0 ? progress : new RebuildProgress(pages[^1], progress.Pages + pages.Count);
    }

    /// <summary>The checkpoint: the last page done (by id) and how many so far.</summary>
    public sealed record RebuildProgress(Guid? After, int Pages);
}
