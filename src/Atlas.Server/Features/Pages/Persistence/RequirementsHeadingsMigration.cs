using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110830, "The 3.6 conversion of requirement blocks to requirement headings runs as the job pages.requirements-headings")]
public sealed class RequirementsHeadingsMigration() : EmbeddedSchemaMigration("2026110830_requirements_headings_job.sql");
