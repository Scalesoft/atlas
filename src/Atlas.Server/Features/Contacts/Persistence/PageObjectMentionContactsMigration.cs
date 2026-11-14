using FluentMigrator;

namespace Atlas.Server.Features.Contacts.Persistence;

[Migration(2026102860, "Mentions name contacts only and follow a page into another space")]
public sealed class PageObjectMentionContactsMigration() : EmbeddedSchemaMigration("2026102860_page_object_mentions_contacts.sql");
