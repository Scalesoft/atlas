using FluentMigrator;

namespace Atlas.Server.Features.Collaboration.Persistence;

[Migration(2026110670, "3.6: the live state's unread Markdown and plain-text projections go")]
public sealed class CollaborationProjectionColumnsMigration() : EmbeddedSchemaMigration("2026110670_collaboration_projection_columns.sql");
