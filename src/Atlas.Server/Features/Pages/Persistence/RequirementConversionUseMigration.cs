using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110850, "The approved budget of the requirement conversion, shared by its job and repair on open")]
public sealed class RequirementConversionUseMigration() : EmbeddedSchemaMigration("2026110850_requirement_conversion_use.sql");
