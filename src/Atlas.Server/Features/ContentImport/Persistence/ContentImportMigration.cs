using FluentMigrator;

namespace Atlas.Server.Features.ContentImport.Persistence;

[Migration(19, "Wiki.js import source identities and temporal checkpoints")]
public sealed class ContentImportMigration() : EmbeddedSchemaMigration("019_content_import.sql");
