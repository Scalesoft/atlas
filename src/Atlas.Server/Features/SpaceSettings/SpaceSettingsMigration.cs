using FluentMigrator;
namespace Atlas.Server;
[Migration(23, "Temporal space metadata and settings")]
public sealed class SpaceSettingsMigration() : EmbeddedSchemaMigration("023_space_settings.sql");
