using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026111010, "A requirement heading's party moves into its attributes (atlas-requirements 4): the job pages.requirements-headings runs again")]
public sealed class RequirementsPartyMoveMigration() : EmbeddedSchemaMigration("2026111010_requirements_party_move.sql");
