using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110960, "A disclosure's title attribute becomes its title node: the job pages.requirements-overview rewrites the current revisions")]
public sealed class DisclosureTitlesMigration() : EmbeddedSchemaMigration("2026110960_disclosure_titles.sql");
