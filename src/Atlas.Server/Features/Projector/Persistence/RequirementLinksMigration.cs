using FluentMigrator;

namespace Atlas.Server.Features.Projector.Persistence;

[Migration(2026103160, "Projector: the party as a contact and who changed each requirement block when")]
public sealed class RequirementLinksMigration() : EmbeddedSchemaMigration("2026103160_projector_requirement_links.sql");
