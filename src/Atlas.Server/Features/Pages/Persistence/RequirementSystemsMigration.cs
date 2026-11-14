using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026111150, "Requirements inserted from the catalog take its service where they have none: the job pages.requirement-systems")]
public sealed class RequirementSystemsMigration() : EmbeddedSchemaMigration("2026111150_requirement_systems.sql");
