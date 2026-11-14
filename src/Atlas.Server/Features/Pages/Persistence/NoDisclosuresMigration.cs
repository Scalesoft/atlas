using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110995, "No disclosure block: the job pages.requirements-overview turns each into a heading and its body in the current revisions")]
public sealed class NoDisclosuresMigration() : EmbeddedSchemaMigration("2026110995_no_disclosures.sql");
