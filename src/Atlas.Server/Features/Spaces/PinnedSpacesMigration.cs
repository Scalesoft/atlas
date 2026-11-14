using FluentMigrator;

namespace Atlas.Server.Features.Spaces;

[Migration(2026092840, "Pinned spaces")]
public sealed class PinnedSpacesMigration() : EmbeddedSchemaMigration("2026092840_pinned_spaces.sql");
