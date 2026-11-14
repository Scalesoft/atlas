using FluentMigrator;

namespace Atlas.Server.Features.Projector.Persistence;

[Migration(2026110820, "3.6: the requirement index reads requirement headings as well as requirement blocks")]
public sealed class RequirementHeadingsMigration() : EmbeddedSchemaMigration("2026110820_projector_requirement_headings.sql");
