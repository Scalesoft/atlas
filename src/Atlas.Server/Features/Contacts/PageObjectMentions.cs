using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Pages;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using FluentMigrator;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts;

[Migration(2026110872, "Contact mentions in pages are derived by code (an outbox delivery and a rebuild job); their triggers and functions go")]
public sealed class PageObjectMentionsToCodeMigration() : EmbeddedSchemaMigration("2026110872_page_object_mentions_to_code.sql");

/// <summary>
/// PageObjectMention (migrations 2026102840, 2026102860, 2026103140): the contacts a page's published and current working revision mention,
/// rebuilt whole for one page from its current state, so a repeated or late refresh changes nothing. It replaces the SQL functions
/// atlas_page_object_mentions and atlas_refresh_page_object_mentions with the same result.
/// </summary>
/// <remarks>
/// A mention is a snapshot of a <c>contactCard</c> or <c>contactInline</c> node (attrs.data.contacts[*]). A snapshot names its contact by
/// <c>objectId</c> (the atlas.contact envelope); one without it is matched by e-mail, ignoring case, only when exactly one live contact of
/// the wiki or of the page's space has one of its addresses, anything else is no mention. An id of another type's object is no mention, an
/// id of no object still is one (a purged contact, read as broken).
/// </remarks>
public sealed class PageObjectMentions(ISession session)
{
    private const char EmailSeparator = '\u001f';

    public async Task RefreshAsync(Guid pageId, CancellationToken ct)
    {
        // Delivery is ordered per page, but a synchronous refresh (a merge, the last-usage warning) may meet one.
        await session.CreateSQLQuery("SELECT count(*) FROM (SELECT pg_advisory_xact_lock(hashtext(:key))) l").SetParameter("key", "atlas.page_mentions:" + pageId).UniqueResultAsync<long>(ct);
        var page = await session.CreateSQLQuery("""SELECT "SpaceId" AS space, "PublishedRevisionId" AS published FROM "Page" WHERE "Id" = :page AND "PurgedAt" IS NULL""")
            .AddScalar("space", NHibernateUtil.Guid).AddScalar("published", NHibernateUtil.Guid).SetParameter("page", pageId).UniqueResultAsync<object[]?>(ct);
        if (page is null)
        {
            return;
        }
        var revisions = await session.CreateSQLQuery("""
                SELECT "Id" AS id, "ContentJson" AS json FROM "PageRevision" WHERE "PageId" = :page AND ("Id" = :published OR "ActiveTo" IS NULL)
                """)
            .AddScalar("id", NHibernateUtil.Guid).AddScalar("json", NHibernateUtil.String)
            .SetParameter("page", pageId).SetParameter("published", page[1], NHibernateUtil.Guid).ListAsync<object[]>(ct);
        await session.CreateSQLQuery("""DELETE FROM "PageObjectMention" WHERE "SourcePageId" = :page""").SetParameter("page", pageId).ExecuteUpdateAsync(ct);
        foreach (var revision in revisions)
        {
            var objects = await MentionedAsync((string)revision[1], (Guid)page[0], ct);
            if (objects.Count > 0)
            {
                await session.CreateSQLQuery("""
                        INSERT INTO "PageObjectMention"("SourcePageId", "ObjectId", "SourceRevisionId")
                        SELECT :page, o, :revision FROM unnest(CAST(string_to_array(:objects, ',') AS uuid[])) o ON CONFLICT DO NOTHING
                        """)
                    .SetParameter("page", pageId).SetParameter("revision", (Guid)revision[0]).SetParameter("objects", string.Join(',', objects)).ExecuteUpdateAsync(ct);
            }
        }
    }

    /// <summary>
    /// The pages whose mentions of <paramref name="contactId"/> may be behind their current revisions (a save whose delivery has not run
    /// yet), refreshed now in page id order: those with a revision written in the last 30 minutes (the outbox's retries span about 21)
    /// that holds its id without a row for it, and those with a row of a revision no longer current. The window also bounds a page that
    /// holds the id outside a snapshot, which never gets a row. A contact only; an e-mail match of a snapshot without the id waits for
    /// its delivery. Only the last-usage warning of a removal or delete asks for it (CountObjectUsage.Fresh), so it never misses a
    /// just-saved mention; the badges' count does not.
    /// </summary>
    public async Task RefreshStaleAsync(Guid contactId, CancellationToken ct)
    {
        var pages = await session.CreateSQLQuery("""
                SELECT s.id FROM (
                  SELECT p."Id" AS id FROM "Page" p JOIN "PageRevision" r ON r."PageId" = p."Id" AND (r."Id" = p."PublishedRevisionId" OR r."ActiveTo" IS NULL)
                  WHERE EXISTS (SELECT 1 FROM "ContentResource" c WHERE c."Id" = :id AND c."TypeId" = :contact)
                    AND r."ActiveFrom" > now() - interval '30 minutes' AND strpos(lower(r."ContentJson"), :text) > 0
                    AND NOT EXISTS (SELECT 1 FROM "PageObjectMention" m WHERE m."SourcePageId" = p."Id" AND m."ObjectId" = :id AND m."SourceRevisionId" = r."Id")
                  UNION
                  SELECT m."SourcePageId" FROM "PageObjectMention" m JOIN "Page" p ON p."Id" = m."SourcePageId"
                  WHERE m."ObjectId" = :id AND m."SourceRevisionId" IS DISTINCT FROM p."PublishedRevisionId"
                    AND NOT EXISTS (SELECT 1 FROM "PageRevision" r WHERE r."Id" = m."SourceRevisionId" AND r."ActiveTo" IS NULL)
                ) s ORDER BY s.id
                """)
            .AddScalar("id", NHibernateUtil.Guid).SetParameter("id", contactId).SetParameter("contact", ContactObjectType.Id)
            .SetParameter("text", contactId.ToString("D")).ListAsync<Guid>(ct);
        foreach (var page in pages)
        {
            await RefreshAsync(page, ct);
        }
    }

    private async Task<IReadOnlyList<Guid>> MentionedAsync(string contentJson, Guid spaceId, CancellationToken ct)
    {
        var mentioned = new HashSet<Guid>();
        var byEmail = new HashSet<string>();
        foreach (var (named, emails) in Snapshots(contentJson))
        {
            if (named is { } id)
            {
                mentioned.Add(id);
            }
            else if (emails.Length > 0)
            {
                byEmail.Add(string.Join(EmailSeparator, emails));
            }
        }
        foreach (var emails in byEmail)
        {
            var matches = await session.CreateSQLQuery("""
                    SELECT r."Id" FROM "ContentResource" r
                    WHERE r."TypeId" = :contact AND r."TrashedAt" IS NULL AND (r."Scope" = 'wiki' OR r."Scope" = 'space' AND r."SpaceId" = :space)
                      AND EXISTS (SELECT 1 FROM jsonb_array_elements_text(CASE WHEN jsonb_typeof(r."Data"->'emails') = 'array' THEN r."Data"->'emails' ELSE '[]' END) e
                        WHERE lower(e) IN (SELECT lower(x) FROM unnest(string_to_array(:emails, chr(31))) x))
                    LIMIT 2
                    """)
                .AddScalar("Id", NHibernateUtil.Guid).SetParameter("contact", ContactObjectType.Id).SetParameter("space", spaceId).SetParameter("emails", emails)
                .ListAsync<Guid>(ct);
            if (matches.Count == 1)
            {
                mentioned.Add(matches[0]);
            }
        }
        if (mentioned.Count == 0)
        {
            return [];
        }
        var others = await session.CreateSQLQuery("""
                SELECT "Id" FROM "ContentResource" WHERE "Id" = ANY(CAST(string_to_array(:ids, ',') AS uuid[])) AND "TypeId" <> :contact
                """)
            .AddScalar("Id", NHibernateUtil.Guid).SetParameter("ids", string.Join(',', mentioned)).SetParameter("contact", ContactObjectType.Id).ListAsync<Guid>(ct);
        return [.. mentioned.Except(others)];
    }

    /// <summary>The contact snapshots of a stored rich document: the contact it names by id, else the addresses to match.</summary>
    public static IReadOnlyList<(Guid? Named, string[] Emails)> Snapshots(string? contentJson)
    {
        var found = new List<(Guid?, string[])>();
        if (string.IsNullOrEmpty(contentJson))
        {
            return found;
        }
        try
        {
            using var json = JsonDocument.Parse(contentJson, MarkdownDocumentValidator.JsonOptions);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("document", out var document))
            {
                Walk(document, found);
            }
        }
        catch (JsonException)
        {
            // Content the editor cannot have stored mentions nobody.
        }
        return found;
    }

    private static void Walk(JsonElement node, List<(Guid?, string[])> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, found);
                }
                break;
            case JsonValueKind.Object:
                if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is "contactCard" or "contactInline"
                    && node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                    && attrs.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var contact in contacts.EnumerateArray().Where(contact => contact.ValueKind == JsonValueKind.Object))
                    {
                        Guid? named = contact.TryGetProperty("objectId", out var id) && id.ValueKind == JsonValueKind.String
                            && PageLinkTargets.TryUuid(id.GetString(), out var parsed) ? parsed : null;
                        var emails = named is null && contact.TryGetProperty("emails", out var list) && list.ValueKind == JsonValueKind.Array
                            ? list.EnumerateArray().Where(email => email.ValueKind == JsonValueKind.String).Select(email => email.GetString()!).ToArray() : [];
                        found.Add((named, emails));
                    }
                }
                // A requirement heading's party (shape §6) mentions its contact as a contact node does.
                if (node.TryGetProperty("type", out var heading) && heading.ValueKind == JsonValueKind.String && heading.GetString() == "heading"
                    && node.TryGetProperty("attrs", out var headingAttrs))
                {
                    foreach (var party in Platform.Content.ContactSnapshots.Named("heading", headingAttrs))
                    {
                        found.Add((PageLinkTargets.TryUuid(party.GetProperty("objectId").GetString(), out var parsed) ? parsed : null, []));
                    }
                }
                foreach (var property in node.EnumerateObject())
                {
                    Walk(property.Value, found);
                }
                break;
        }
    }
}

/// <summary>The mention projection's own delivery of <see cref="ContentRevised"/>: every announcement of a page, its working revisions
/// too (a draft's mentions count for „Použito na“), its publication, move, trash and the silent <c>rebuild</c>.</summary>
public sealed record PageObjectMentionRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message.ContentType == PagesAtlasModule.ContentType;
}

public static class PageObjectMentionRevisionHandler
{
    public static Task Handle(PageObjectMentionRevision delivery, PageObjectMentions mentions, CancellationToken ct)
        => PageObjectMentionRevision.Takes(delivery.Event) ? mentions.RefreshAsync(delivery.Event.ResourceId, ct) : Task.CompletedTask;
}

/// <summary>
/// The contact mentions from the published and working revisions of all pages, in short checkpointed batches (migration 2026110872 queues
/// it; an administrator may run it again from Úlohy, and a backup restore queues it): idempotent, each page rebuilt from its current state.
/// </summary>
public sealed class PageObjectMentionRebuildJob : IJobDefinition
{
    public const string JobId = "contacts.mentions-rebuild";

    public string Id => JobId;

    public string Title => "Přestavba zmínek kontaktů ve stránkách";

    public string Description => "Znovu přečte kontakty zmíněné ve stránkách po obnově zálohy nebo po změně pravidel, podle kterých se čtou.";

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
                    .AddScalar("Id", NHibernateUtil.Guid).SetParameter("after", after).SetParameter("batch", PageLinkRebuildJob.Batch).ListAsync<Guid>(token);
                var mentions = new PageObjectMentions(session);
                foreach (var page in pages)
                {
                    await mentions.RefreshAsync(page, token);
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
        context.Log.LogInformation("Zmínky přečteny z {Count} stránek", progress.Pages);
    }
}
