using FluentMigrator;

namespace Atlas.Server.Features.Collaboration;

[Migration(2026110571, "Where each live collaboration update came from (3.6-H)")]
public sealed class CollaborationUpdateOriginMigration() : EmbeddedSchemaMigration("2026110571_collaboration_update_origin.sql");
