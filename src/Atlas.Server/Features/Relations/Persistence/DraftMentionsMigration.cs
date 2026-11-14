using FluentMigrator;

namespace Atlas.Server.Features.Relations.Persistence;

[Migration(2026103140, "The mention projection covers working revisions")]
public sealed class DraftMentionsMigration() : EmbeddedSchemaMigration("2026103140_page_object_mentions_drafts.sql");
