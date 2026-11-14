using FluentMigrator;

namespace Atlas.Server.Platform.Persistence;

[Migration(2026110990, "No logic in the database: the last function goes; the schema holds no trigger, function or procedure of ours")]
public sealed class NoLogicInDatabaseMigration() : EmbeddedSchemaMigration("2026110990_no_logic_in_database.sql");
