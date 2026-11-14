using FluentMigrator;
namespace Atlas.Server;

[Migration(17, "Preserve collaboration log across additive schema generations")]
public sealed class AttachmentSchemaMigration() : EmbeddedSchemaMigration("017_collaboration_generation_cascade.sql");
