using System.Text.Json.Nodes;
using Atlas.Server.Features.Pages;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Features.Linking.Commands;
using Atlas.Server.Platform.Persistence;
using FluentMigrator;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Linking;

[Migration(2026110310, "The graph of what content names: ContentReference, its sources and the dismissed offers")]
public sealed class ContentReferenceMigration() : EmbeddedSchemaMigration("2026110310_content_reference.sql");

[Migration(2026110320, "Owner decision: the page alone says what is linked; the dismissed link offers go")]
public sealed class ContentReferenceDismissalDropMigration() : EmbeddedSchemaMigration("2026110320_drop_content_reference_dismissal.sql");

/// <summary>
/// Writes <c>ContentReference</c> (migration 2026110310): a source's rows replaced whole from its current state, so a repeated or late
/// refresh changes nothing. A page gives its working and published revisions and its comments, a board its items' text and its
/// comments; each with the extraction lexicon of its place (<see cref="PlaceLexiconCache"/>), whose version it stores.
/// </summary>
public sealed class ContentReferenceStore(ISession session, PlaceNames names, PlaceLexiconCache places)
{
    /// <summary>What a page's references were read from: its working and published revision and its comments.</summary>
    private const string PageStamp = """
        concat_ws(':', (SELECT w."Id" FROM "PageRevision" w WHERE w."PageId" = p."Id" AND w."ActiveTo" IS NULL), p."PublishedRevisionId", (SELECT count(*) || '/'
            || coalesce(CAST(max(greatest(c."CreatedAt", c."EditedAt", c."DeletedAt")) AS text), '') FROM "Comment" c WHERE c."ObjectId" = p."Id"))
        """;

    private const string BoardStamp = """
        concat_ws(':', b."Sequence", (SELECT count(*) || '/'
            || coalesce(CAST(max(greatest(c."CreatedAt", c."EditedAt", c."DeletedAt")) AS text), '') FROM "Comment" c WHERE c."ObjectId" = b."Id"))
        """;

    /// <summary>The sources one sweep extracts at most.</summary>
    public const int SweepBatch = 200;

    public async Task RefreshPageAsync(Guid pageId, PlaceLexicon? lexicon, CancellationToken ct)
    {
        var page = await session.CreateSQLQuery($"""
                SELECT p."SpaceId" AS space, w."Id" AS working, w."ContentJson" AS "workingJson", r."Id" AS published, r."ContentJson" AS "publishedJson",
                    {PageStamp} AS stamp, p."PublishedRevisionId" IS NOT DISTINCT FROM w."Id" AS "workingPublished"
                FROM "Page" p JOIN "PageRevision" w ON w."PageId" = p."Id" AND w."ActiveTo" IS NULL
                LEFT JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId" AND r."Id" <> w."Id"
                WHERE p."Id" = :id AND NOT p."IsTrashed"
                """)
            .SetParameter("id", pageId).UniqueResultAsync<object[]?>(ct);
        if (page is null)
        {
            await RemoveAsync(pageId, ct);
            return;
        }
        var spaceId = (Guid)page[0];
        lexicon ??= await places.GetAsync(spaceId, names, ct);
        var rows = new List<(string Part, string Kind, IReadOnlyList<ContentOccurrence> Occurrences)>
        {
            ("rev:" + page[1], (bool)page[6] ? "published" : "working",
                ContentReferences.Read((string)page[2], lexicon, pageId).Occurrences),
        };
        if (page[3] is Guid published)
        {
            rows.Add(("rev:" + published, "published", ContentReferences.Read((string)page[4], lexicon, pageId).Occurrences));
        }
        rows.AddRange(await CommentsAsync(pageId, lexicon, ct));
        await WriteAsync(pageId, spaceId, (string)page[5], lexicon.Version, rows, ct);
    }

    public async Task RefreshBoardAsync(Guid boardId, PlaceLexicon? lexicon, CancellationToken ct)
    {
        var board = await session.CreateSQLQuery($"""SELECT b."SpaceId" AS space, {BoardStamp} AS stamp FROM "Board" b WHERE b."Id" = :id AND NOT b."IsTrashed" """)
            .SetParameter("id", boardId).UniqueResultAsync<object[]?>(ct);
        if (board is null)
        {
            await RemoveAsync(boardId, ct);
            return;
        }
        var spaceId = (Guid)board[0];
        lexicon ??= await places.GetAsync(spaceId, names, ct);
        var items = await session.CreateSQLQuery("""
                SELECT "Id", "Text" FROM "BoardItem" WHERE "BoardId" = :id AND NOT "IsDeleted" AND "Text" IS NOT NULL AND btrim("Text") <> '' ORDER BY "Id"
                """)
            .SetParameter("id", boardId).ListAsync<object[]>(ct);
        var rows = items.Select(item => ("item:" + item[0], "none", Markdown((string)item[1], lexicon))).ToList();
        rows.AddRange(await CommentsAsync(boardId, lexicon, ct));
        await WriteAsync(boardId, spaceId, (string)board[1], lexicon.Version, rows, ct);
    }

    /// <summary>
    /// Extracts up to <see cref="SweepBatch"/> sources whose state or place lexicon changed since their extraction (every source right
    /// after the migration and after a restore, all of a place after its names changed, comments and board items within a pass) and
    /// forgets the rows of sources that went. Returns how many it extracted.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var done = 0;
        // One pass at a time (the job's and a test's): the second waits and then finds the first one's work done.
        await session.CreateSQLQuery("SELECT count(*) FROM (SELECT pg_advisory_xact_lock(hashtext('atlas.content_reference_sweep'))) l").UniqueResultAsync<long>(ct);
        var spaces = await session.CreateSQLQuery("""SELECT "Id" FROM "Space" ORDER BY "Id" """).AddScalar("Id", NHibernateUtil.Guid).ListAsync<Guid>(ct);
        foreach (var space in spaces)
        {
            if (done >= SweepBatch)
            {
                break;
            }
            var lexicon = places.Put(space, await names.ReadAsync(space, null, ct));
            var stale = await session.CreateSQLQuery($"""
                    SELECT p."Id", false AS board FROM "Page" p LEFT JOIN "ContentReferenceSource" s ON s."SourceId" = p."Id"
                    WHERE p."SpaceId" = :space AND NOT p."IsTrashed"
                        AND (s."SourceId" IS NULL OR s."LexiconVersion" <> :version OR s."SpaceId" <> p."SpaceId" OR s."Stamp" <> {PageStamp})
                    UNION ALL
                    SELECT b."Id", true FROM "Board" b LEFT JOIN "ContentReferenceSource" s ON s."SourceId" = b."Id"
                    WHERE b."SpaceId" = :space AND NOT b."IsTrashed"
                        AND (s."SourceId" IS NULL OR s."LexiconVersion" <> :version OR s."SpaceId" <> b."SpaceId" OR s."Stamp" <> {BoardStamp})
                    UNION ALL
                    SELECT s."SourceId", NULL FROM "ContentReferenceSource" s WHERE s."SpaceId" = :space
                        AND NOT EXISTS (SELECT 1 FROM "Page" p WHERE p."Id" = s."SourceId" AND NOT p."IsTrashed")
                        AND NOT EXISTS (SELECT 1 FROM "Board" b WHERE b."Id" = s."SourceId" AND NOT b."IsTrashed")
                    LIMIT :limit
                    """)
                .AddScalar("Id", NHibernateUtil.Guid).AddScalar("board", NHibernateUtil.Boolean)
                .SetParameter("space", space).SetParameter("version", lexicon.Version).SetParameter("limit", SweepBatch - done)
                .ListAsync<object[]>(ct);
            foreach (var row in stale)
            {
                var id = (Guid)row[0];
                await (row[1] switch
                {
                    true => RefreshBoardAsync(id, lexicon, ct),
                    false => RefreshPageAsync(id, lexicon, ct),
                    _ => RemoveAsync(id, ct),
                });
                done++;
            }
        }
        return done;
    }

    private async Task<List<(string, string, IReadOnlyList<ContentOccurrence>)>> CommentsAsync(Guid objectId, PlaceLexicon lexicon, CancellationToken ct)
    {
        var comments = await session.CreateSQLQuery("""
                SELECT "Id", "Body" FROM "Comment" WHERE "ObjectId" = :id AND "DeletedAt" IS NULL ORDER BY "CreatedAt", "Id"
                """)
            .SetParameter("id", objectId).ListAsync<object[]>(ct);
        return comments.Select(comment => ("comment:" + comment[0], "none", Markdown((string)comment[1], lexicon))).ToList();
    }

    /// <summary>A comment's or a board item's Markdown as the editor's document (MarkdownToRich); text it cannot read has nothing.</summary>
    private static IReadOnlyList<ContentOccurrence> Markdown(string markdown, PlaceLexicon lexicon)
    {
        try
        {
            var blocks = MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins).Blocks;
            return ContentReferences.Read(new JsonObject { ["type"] = "doc", ["content"] = blocks }, lexicon);
        }
        catch (ApiException)
        {
            return [];
        }
    }

    private async Task WriteAsync(Guid sourceId, Guid spaceId, string stamp, string version,
        IReadOnlyList<(string Part, string Kind, IReadOnlyList<ContentOccurrence> Occurrences)> sources, CancellationToken ct)
    {
        var rows = new JsonArray();
        foreach (var (part, kind, occurrences) in sources)
        {
            foreach (var occurrence in occurrences)
            {
                rows.Add(new JsonObject
                {
                    ["part"] = part, ["revisionKind"] = kind, ["ordinal"] = occurrence.Ordinal, ["kind"] = occurrence.Kind,
                    ["relation"] = occurrence.Kind == "page" ? "link" : "mention", ["state"] = occurrence.State,
                    ["targetId"] = occurrence.TargetId?.ToString(), ["targetKey"] = occurrence.TargetKey,
                    ["candidates"] = occurrence.Candidates.Count == 0 ? null : new JsonArray(occurrence.Candidates.Select(key => (JsonNode)key).ToArray()),
                    ["text"] = occurrence.Text, ["anchor"] = occurrence.Anchor, ["context"] = occurrence.Context, ["recognizer"] = occurrence.Recognizer,
                });
            }
        }
        // The source row first: its lock orders two refreshes of one source (the outbox and the sweep), so neither inserts beside the other.
        await session.CreateSQLQuery("""
                INSERT INTO "ContentReferenceSource"("SourceId","SpaceId","Stamp","LexiconVersion","ExtractedAt") VALUES (:id, :space, :stamp, :version, now())
                ON CONFLICT ("SourceId") DO UPDATE SET "SpaceId" = EXCLUDED."SpaceId", "Stamp" = EXCLUDED."Stamp", "LexiconVersion" = EXCLUDED."LexiconVersion", "ExtractedAt" = now();
                DELETE FROM "ContentReference" WHERE "SourceId" = :id;
                INSERT INTO "ContentReference"("SourceId","SourcePart","RevisionKind","Ordinal","Kind","Relation","State","TargetId","TargetKey","Candidates","Text","Anchor","Context","Recognizer")
                SELECT :id, x."part", x."revisionKind", x."ordinal", x."kind", x."relation", x."state", x."targetId", x."targetKey", x."candidates", x."text", x."anchor", x."context", x."recognizer"
                FROM jsonb_to_recordset(CAST(:rows AS jsonb)) AS x("part" text, "revisionKind" text, "ordinal" integer, "kind" text, "relation" text, "state" text,
                    "targetId" uuid, "targetKey" text, "candidates" text[], "text" text, "anchor" text, "context" text, "recognizer" text)
                """)
            .SetParameter("id", sourceId).SetParameter("space", spaceId).SetParameter("stamp", stamp).SetParameter("version", version)
            .SetParameter("rows", rows.ToJsonString())
            .ExecuteUpdateAsync(ct);
    }

    private async Task RemoveAsync(Guid sourceId, CancellationToken ct) =>
        await session.CreateSQLQuery("""
                DELETE FROM "ContentReference" WHERE "SourceId" = :id;
                DELETE FROM "ContentReferenceSource" WHERE "SourceId" = :id;
                """)
            .SetParameter("id", sourceId).ExecuteUpdateAsync(ct);
}

/// <summary>The graph's own delivery of <see cref="ContentRevised"/> of pages: it reads the page's current state, so an older delivery
/// after a newer one changes nothing.</summary>
public sealed record ContentReferenceRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message.ContentType == PagesAtlasModule.ContentType;
}

public static class ContentReferenceRevisionHandler
{
    public static Task Handle(ContentReferenceRevision delivery, ContentReferenceStore store, CancellationToken ct)
        => ContentReferenceRevision.Takes(delivery.Event) ? store.RefreshPageAsync(delivery.Event.ResourceId, null, ct) : Task.CompletedTask;
}

public static class LinkingServices
{
    public static IServiceCollection AddLinking(this IServiceCollection services)
    {
        services.AddScoped<PlaceNames>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PlaceLexiconCache>();
        services.AddScoped<ContentReferenceStore>();
        services.AddScoped<EntityLinker>();
        return services;
    }

    /// <summary>The sweep's job. Test hosts leave it out and send <see cref="SweepContentReferences"/>.</summary>
    public static IServiceCollection AddLinkingBackgroundServices(this IServiceCollection services) => services.AddJob<ContentReferenceJob>();
}
