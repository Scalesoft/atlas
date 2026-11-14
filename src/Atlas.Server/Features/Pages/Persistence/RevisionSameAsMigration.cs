using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026111020, "A version that ends exactly where it started names the revision it equals and leaves the page history")]
public sealed class RevisionSameAsMigration() : EmbeddedSchemaMigration("2026111020_page_revision_same_as.sql");
