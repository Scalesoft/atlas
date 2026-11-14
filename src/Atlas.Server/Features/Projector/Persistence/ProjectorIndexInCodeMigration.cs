using FluentMigrator;

namespace Atlas.Server.Features.Projector.Persistence;

[Migration(2026110870, "The requirement index is kept in code: its triggers and functions go, the job projector.requirements-index rebuilds it")]
public sealed class ProjectorIndexInCodeMigration() : EmbeddedSchemaMigration("2026110870_projector_index_in_code.sql");
