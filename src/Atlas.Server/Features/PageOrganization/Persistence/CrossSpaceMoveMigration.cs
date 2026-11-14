using FluentMigrator;

namespace Atlas.Server.Features.PageOrganization.Persistence;

[Migration(24, "Atomic cross-space moves and saved collaboration checkpoints")]
public sealed class CrossSpaceMoveMigration() : EmbeddedSchemaMigration("024_cross_space_move.sql");
