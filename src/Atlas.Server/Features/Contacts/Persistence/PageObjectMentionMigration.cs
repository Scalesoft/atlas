using FluentMigrator;

namespace Atlas.Server.Features.Contacts.Persistence;

[Migration(2026102840, "Contacts mentioned in published pages, a projection of the published revision")]
public sealed class PageObjectMentionMigration() : EmbeddedSchemaMigration("2026102840_page_object_mentions.sql");
