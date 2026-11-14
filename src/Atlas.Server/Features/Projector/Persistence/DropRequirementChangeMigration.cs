using FluentMigrator;

namespace Atlas.Server.Features.Projector.Persistence;

[Migration(2026110690, "3.6: a requirement's history is read from the text and the version history; its change log goes")]
public sealed class DropRequirementChangeMigration() : EmbeddedSchemaMigration("2026110690_drop_projector_requirement_change.sql");
