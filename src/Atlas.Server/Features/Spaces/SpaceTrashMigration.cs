using FluentMigrator;

namespace Atlas.Server.Features.Spaces;

[Migration(2026102710, "Space trash")]
public sealed class SpaceTrashMigration() : EmbeddedSchemaMigration("2026102710_space_trash.sql");
