using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110650, "3.6-I: the publication of a page (Ručně, Automaticky, Živě) with the default its space gives")]
public sealed class PublicationModeMigration() : EmbeddedSchemaMigration("2026110650_publication_mode.sql");
