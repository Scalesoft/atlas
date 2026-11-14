using FluentMigrator;

namespace Atlas.Server.Features.TrashCleanup;

[Migration(2026102997, "Cleanup of orphaned items: the wiki's rule and each space's")]
public sealed class TrashCleanupMigration() : EmbeddedSchemaMigration("2026102997_trash_cleanup.sql");
