using FluentMigrator;

namespace Atlas.Server.Features.KnowledgeMaintenance;

[Migration(2026100810, "Space administrators can dismiss duplicate page pairs")]
public sealed class DuplicateDismissalMigration() : EmbeddedSchemaMigration("2026100810_ai_duplicate_dismissal.sql");
