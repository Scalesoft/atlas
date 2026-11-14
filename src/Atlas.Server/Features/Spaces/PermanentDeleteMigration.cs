using FluentMigrator;

namespace Atlas.Server.Features.Spaces;

[Migration(2026102975, "Permanent delete of pages and spaces and its audit")]
public sealed class PermanentDeleteMigration() : EmbeddedSchemaMigration("2026102975_permanent_delete.sql");

[Migration(2026103011, "A purged page keeps no requirement index and no change summary")]
public sealed class PurgedPageLeftoversMigration() : EmbeddedSchemaMigration("2026103011_purged_page_leftovers.sql");
