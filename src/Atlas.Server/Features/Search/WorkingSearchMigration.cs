using FluentMigrator;

namespace Atlas.Server;

[Migration(26, "Indexed current working revision search")]
public sealed class WorkingSearchMigration() : EmbeddedSchemaMigration("026_working_search.sql");
