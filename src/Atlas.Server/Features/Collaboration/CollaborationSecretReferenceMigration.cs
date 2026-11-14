using FluentMigrator;

namespace Atlas.Server.Features.Collaboration;

[Migration(2026092710, "Secret references admitted into live collaboration")]
public sealed class CollaborationSecretReferenceMigration() : EmbeddedSchemaMigration("2026092710_collaboration_secret_references.sql");
