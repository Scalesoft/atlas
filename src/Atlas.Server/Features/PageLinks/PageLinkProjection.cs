using System.Text.Json;
using Atlas.Markdown.Sync;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Pages;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using FluentMigrator;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageLinks;

[Migration(2026110871, "Page links are derived by code (an outbox delivery and a rebuild job); their triggers and functions go")]
public sealed class PageLinksToCodeMigration() : EmbeddedSchemaMigration("2026110871_page_links_to_code.sql");

/// <summary>
/// The single definition of a page link in stored rich content (ContentJson, never the lossy Markdown); the projection, the publication
/// check, the export, the shared page, the page bundle, the MCP read and the linking engine (ContentReferences) share it. A link is a
/// <c>pageReference</c> node (attrs.id) or any string <c>href</c> holding <c>#/pages/&lt;uuid&gt;</c>: relative, <c>/#/...</c>, or an
/// absolute URL, optionally followed by <c>?heading=</c>. Values that are not a UUID are ignored. It replaces the SQL function
/// atlas_page_link_targets, with the same result. <c>atlas://pages/</c> is no stored form: Markdown stores it as <c>#/pages/</c>, and the
/// editor's links take only http, https, mailto and tel.
/// </summary>
public static partial class PageLinkTargets
{
    [GeneratedRegex(@"#/pages/([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})(?:[?/#]|\z)")]
    private static partial Regex Href();

    [GeneratedRegex(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$")]
    private static partial Regex Uuid();

    /// <summary>A UUID exactly as the SQL function read one (hyphenated, hex, nothing around it), unlike Guid.TryParse.</summary>
    public static bool TryUuid(string? text, out Guid id)
    {
        id = default;
        return text is not null && Uuid().IsMatch(text) && Guid.TryParse(text, out id);
    }

    /// <summary>The page an href opens, else null.</summary>
    public static Guid? OfHref(string? href) => href is not null && Href().Match(href) is { Success: true } match ? Guid.Parse(match.Groups[1].Value) : null;

    /// <summary>The pages one object node of a document links to by itself, its children not included: a chip's id and, with
    /// <paramref name="hrefs"/>, its href.</summary>
    public static void Add(JsonElement node, ISet<Guid> found, bool hrefs = true)
    {
        if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "pageReference"
            && node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object
            && attrs.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            && TryUuid(id.GetString(), out var target))
        {
            found.Add(target);
        }
        if (hrefs && node.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String && OfHref(href.GetString()) is { } linked)
        {
            found.Add(linked);
        }
    }

    /// <summary>The pages a stored envelope links to; without <paramref name="hrefs"/> only its chips (whose titles the MCP read shows).</summary>
    public static IReadOnlyList<Guid> Of(string? contentJson, bool hrefs = true)
    {
        var found = new HashSet<Guid>();
        if (!string.IsNullOrEmpty(contentJson))
        {
            try
            {
                using var json = JsonDocument.Parse(contentJson, MarkdownDocumentValidator.JsonOptions);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("document", out var document))
                {
                    Walk(document, found, hrefs);
                }
            }
            catch (JsonException)
            {
                // Content the editor cannot have stored links nowhere.
            }
        }
        return [.. found];
    }

    private static void Walk(JsonElement node, HashSet<Guid> found, bool hrefs)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, found, hrefs);
                }
                break;
            case JsonValueKind.Object:
                Add(node, found, hrefs);
                foreach (var property in node.EnumerateObject())
                {
                    Walk(property.Value, found, hrefs);
                }
                break;
        }
    }
}

/// <summary>
/// PageLink (migration 2026100420): "the published revision of page S links to page T", rebuilt whole for one page from its current
/// state, so a repeated or late refresh changes nothing. Identities only; readers decide on the actor's current rights.
/// </summary>
public sealed class PageLinkProjection(ISession session)
{
    public async Task RefreshAsync(Guid pageId, CancellationToken ct)
    {
        // Delivery is ordered per page, but a synchronous refresh (a conversion) may meet one: the second waits and finds the first's work done.
        await session.CreateSQLQuery("SELECT count(*) FROM (SELECT pg_advisory_xact_lock(hashtext(:key))) l").SetParameter("key", "atlas.page_links:" + pageId).UniqueResultAsync<long>(ct);
        var page = await session.CreateSQLQuery("""
                SELECT p."PurgedAt" IS NOT NULL AS purged, r."Id" AS id, r."ContentJson" AS json
                FROM "Page" p LEFT JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId" WHERE p."Id" = :page
                """)
            .AddScalar("purged", NHibernateUtil.Boolean).AddScalar("id", NHibernateUtil.Guid).AddScalar("json", NHibernateUtil.String)
            .SetParameter("page", pageId).UniqueResultAsync<object[]?>(ct);
        // A purge removed the rows under this lock and blanked the revisions: a late delivery changes nothing.
        if (page is not null && (bool)page[0])
        {
            return;
        }
        await session.CreateSQLQuery("""DELETE FROM "PageLink" WHERE "SourcePageId" = :page""").SetParameter("page", pageId).ExecuteUpdateAsync(ct);
        if (page?[1] is not Guid revision || PageLinkTargets.Of((string)page[2]).Where(target => target != pageId).ToArray() is not { Length: > 0 } targets)
        {
            return;
        }
        await session.CreateSQLQuery("""
                INSERT INTO "PageLink"("SourcePageId", "TargetPageId", "SourceRevisionId")
                SELECT :page, t, :revision FROM unnest(CAST(string_to_array(:targets, ',') AS uuid[])) t
                WHERE EXISTS (SELECT 1 FROM "Page" p WHERE p."Id" = :page)
                """)
            .SetParameter("page", pageId).SetParameter("revision", revision).SetParameter("targets", string.Join(',', targets)).ExecuteUpdateAsync(ct);
    }
}

/// <summary>The page links' own delivery of <see cref="ContentRevised"/>: a page's publication, trash, restore, move, template flag or
/// the silent <c>rebuild</c>, not its working revisions (the projection holds the publication only).</summary>
public sealed record PageLinkRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message is { ContentType: PagesAtlasModule.ContentType, RevisionKind: not "working" };
}

public static class PageLinkRevisionHandler
{
    public static Task Handle(PageLinkRevision delivery, PageLinkProjection links, CancellationToken ct)
        => PageLinkRevision.Takes(delivery.Event) ? links.RefreshAsync(delivery.Event.ResourceId, ct) : Task.CompletedTask;
}

/// <summary>Where a rebuild stands: pages refreshed up to <paramref name="After"/> by id.</summary>
public sealed record PageRebuildProgress(Guid After, int Pages);

/// <summary>
/// The page links from the published revisions of all pages, in short checkpointed batches (migration 2026110871 queues it; an
/// administrator may run it again from Úlohy, and a backup restore queues it): idempotent, each page rebuilt from its current state.
/// </summary>
public sealed class PageLinkRebuildJob : IJobDefinition
{
    public const string JobId = "pages.links-rebuild";
    public const int Batch = 100;

    public string Id => JobId;

    public string Title => "Přestavba odkazů mezi stránkami";

    public string Description => "Znovu přečte odkazy mezi stránkami z publikovaných verzí po obnově zálohy nebo po změně pravidel, podle kterých se čtou.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var progress = context.Progress<PageRebuildProgress>() ?? new PageRebuildProgress(Guid.Empty, 0);
        while (true)
        {
            var after = progress.After;
            var done = progress.Pages;
            var batch = await context.StepAsync(async (session, token) =>
            {
                var pages = await session.CreateSQLQuery("""SELECT "Id" FROM "Page" WHERE "Id" > :after ORDER BY "Id" LIMIT :batch""")
                    .AddScalar("Id", NHibernateUtil.Guid).SetParameter("after", after).SetParameter("batch", Batch).ListAsync<Guid>(token);
                var links = new PageLinkProjection(session);
                foreach (var page in pages)
                {
                    await links.RefreshAsync(page, token);
                }
                return new PageRebuildProgress(pages.Count == 0 ? after : pages[^1], done + pages.Count);
            }, ct);
            context.Summarize(new { pages = batch.Pages });
            if (batch.After == after)
            {
                break;
            }
            progress = batch;
        }
        context.Log.LogInformation("Odkazy přečteny z {Count} stránek", progress.Pages);
    }
}
