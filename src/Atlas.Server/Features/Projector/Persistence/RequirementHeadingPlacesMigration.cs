using FluentMigrator;

namespace Atlas.Server.Features.Projector.Persistence;

[Migration(2026110840, "3.6: the requirement index reads the dependency and facts lines at their fixed places under the heading")]
public sealed class RequirementHeadingPlacesMigration() : EmbeddedSchemaMigration("2026110840_projector_requirement_heading_places.sql");
