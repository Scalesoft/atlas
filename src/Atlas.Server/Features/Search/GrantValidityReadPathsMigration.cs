using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026092503, "Grant validity in search and read projections")]
public sealed class GrantValidityReadPathsMigration()
    : EmbeddedSchemaMigration("2026092503_grant_validity_read_paths.sql");
