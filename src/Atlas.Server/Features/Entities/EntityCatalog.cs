using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Pages;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using FluentMigrator;
using NHibernate;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Entities;

[Migration(2026102950, "The entity catalog of contacts, filled from ContentRevised")]
public sealed class EntityCatalogMigration() : EmbeddedSchemaMigration("2026102950_entity_catalog.sql");

[Migration(2026102985, "The entity catalog's rebuild announces ContentRevised rebuild, not a publication")]
public sealed class EntityCatalogRebuildKindMigration() : EmbeddedSchemaMigration("2026102985_entity_catalog_rebuild_kind.sql");

[Migration(2026103100, "The entity catalog rebuilt for the contact identity with phones")]
public sealed class EntityIdentityPhoneMigration() : EmbeddedSchemaMigration("2026103100_entity_identity_phone.sql");

[Migration(2026110101, "The entity catalog rebuilt without unresolved contacts")]
public sealed class EntityCatalogUnresolvedMigration() : EmbeddedSchemaMigration("2026110101_entity_catalog_unresolved.sql");

/// <summary>The entity kinds of 3.0 (plan §14.1); <c>organization</c> and the module kinds follow.</summary>
public static class EntityKinds
{
    public const string Contact = "contact";
}

/// <summary>
/// Writes the entity catalog (plan §14.2, migration 2026102950): a resource's occurrences replaced from its current state, so a repeated
/// or late delivery changes nothing. A page gets those of its working and of its published revision, a directory contact one of its data.
/// </summary>
public sealed class EntityCatalog(ISession session)
{
    public async Task RefreshAsync(Guid resourceId, CancellationToken ct)
    {
        var sources = await session.CreateSQLQuery("""
                SELECT 'working' AS kind, w."ContentJson" AS body, false AS object FROM "PageRevision" w WHERE w."PageId" = :id AND w."ActiveTo" IS NULL
                UNION ALL
                SELECT 'published', r."ContentJson", false FROM "Page" p JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId" WHERE p."Id" = :id
                UNION ALL
                SELECT 'published', CAST(c."Data" AS text), true FROM "ContentResource" c WHERE c."Id" = :id AND c."TypeId" = :contact AND c."Data" IS NOT NULL
                """)
            .AddScalar("kind", NHibernateUtil.String).AddScalar("body", NHibernateUtil.String).AddScalar("object", NHibernateUtil.Boolean)
            .SetParameter("id", resourceId).SetParameter("contact", ContactObjectType.Id)
            .ListAsync<object[]>(ct);
        var rows = new JsonArray();
        foreach (var source in sources)
        {
            var (kind, body) = ((string)source[0], (string)source[1]);
            IEnumerable<(JsonObject Contact, string? Anchor)> contacts = (bool)source[2]
                ? [(DirectoryContact(resourceId, body), null)]
                : ContactSnapshots.Read(body);
            foreach (var (contact, anchor) in contacts)
            {
                rows.Add(new JsonObject
                {
                    ["revisionKind"] = kind, ["identityKey"] = EntityIdentity.Of(contact), ["attributes"] = contact, ["anchor"] = anchor,
                });
            }
        }
        // The resource's delivery is ordered (OrderingKey = its id), so no other refresh of it runs meanwhile. The envelope is locked
        // first, as a permanent delete locks it before it deletes the occurrences: the other order deadlocks with a purge (PER3-2).
        await session.CreateSQLQuery("""
                SELECT 1 FROM "ContentResource" WHERE "Id" = :id FOR KEY SHARE;
                DELETE FROM "EntityOccurrence" WHERE "ResourceId" = :id;
                INSERT INTO "EntityOccurrence"("ResourceId","RevisionKind","Kind","IdentityKey","Attributes","Anchor")
                SELECT :id, x."revisionKind", :kind, x."identityKey", x."attributes", x."anchor"
                FROM jsonb_to_recordset(CAST(:rows AS jsonb)) AS x("revisionKind" text, "identityKey" text, "attributes" jsonb, "anchor" text)
                WHERE EXISTS (SELECT 1 FROM "ContentResource" r WHERE r."Id" = :id)
                """)
            .SetParameter("id", resourceId).SetParameter("kind", EntityKinds.Contact).SetParameter("rows", rows.ToJsonString())
            .ExecuteUpdateAsync(ct);
    }

    /// <summary>A directory contact's data as a snapshot of it: the plugin's fields and its own id, which snapshots name it by.</summary>
    private static JsonObject DirectoryContact(Guid id, string data)
    {
        using var document = JsonDocument.Parse(data);
        var contact = ContactSnapshots.FieldsOf(document.RootElement);
        contact["objectId"] = id.ToString();
        // The directory's own aliases („MMu“, „Mára“): the catalog finds and shows the person by them; snapshots in pages have none.
        if (document.RootElement.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
        {
            contact["aliases"] = JsonNode.Parse(aliases.GetRawText());
        }
        return contact;
    }
}

/// <summary>The catalog's own delivery of <see cref="ContentRevised"/>: it rebuilds from the resource's current state, so an older
/// delivery after a newer one changes nothing.</summary>
public sealed record EntityCatalogRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message.ContentType is PagesAtlasModule.ContentType or ContactObjectType.Id;
}

/// <summary>The catalog's consumer of <see cref="ContentRevised"/>: pages and directory contacts; other content has no entities yet.</summary>
public static class EntityCatalogHandler
{
    public static Task Handle(EntityCatalogRevision delivery, EntityCatalog catalog, CancellationToken ct)
        => EntityCatalogRevision.Takes(delivery.Event) ? catalog.RefreshAsync(delivery.Event.ResourceId, ct) : Task.CompletedTask;
}

/// <summary>
/// The catalog's full rebuild (3.3 plan §4.8, owner decision O2): ContentRevised <c>rebuild</c> of every directory contact and every page
/// whose working or published revision holds a contact (AnnounceCatalogRebuild), so each consumer rebuilds from the current state
/// and tells nobody. Bare SQL that writes revisions or changes how they are read requests it with a queued row (a restore, a migration),
/// as migrations 2026102950, 2026103100 and 2026110101 enqueued the same rebuild before it existed:
/// <c>INSERT INTO "JobRun"("JobId","Trigger","Reason","State") VALUES ('entities.catalog-rebuild','event','…','queued') ON CONFLICT DO NOTHING</c>.
/// </summary>
public sealed class EntityCatalogRebuildJob : IJobDefinition
{
    public const string JobId = "entities.catalog-rebuild";

    public string Id => JobId;

    public string Title => "Přestavba katalogu kontaktů";

    public string Description => "Znovu přečte kontakty ze stránek a z adresáře po obnově zálohy nebo po změně pravidel, podle kterých se čtou.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var announced = await context.Services.GetRequiredService<IMessageBus>().InvokeAsync<int>(new Commands.AnnounceCatalogRebuild(), ct);
        context.Log.LogInformation("Announced {Count} resources for the rebuild", announced);
        context.Summarize(new { announced });
    }
}
