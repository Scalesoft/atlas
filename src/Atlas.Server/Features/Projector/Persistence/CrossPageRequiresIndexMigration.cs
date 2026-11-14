using FluentMigrator;

namespace Atlas.Server.Features.Projector.Persistence;

[Migration(2026103196, "Projector: the blocks requiring another page's are read by a partial index")]
public sealed class CrossPageRequiresIndexMigration() : EmbeddedSchemaMigration("2026103196_projector_cross_page_requires.sql");
