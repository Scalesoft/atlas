using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026112410, "The profile choice of the colour in live pages and boards")]
public sealed class AppearanceCollaboratorColorMigration() : EmbeddedSchemaMigration("2026112410_appearance_collaborator_color.sql");
