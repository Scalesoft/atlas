using FluentMigrator;

namespace Atlas.Server.Features.Spaces;

[Migration(2026092502)]
public sealed class GrantValidityMigration() : EmbeddedSchemaMigration("2026092502_grant_validity.sql");
