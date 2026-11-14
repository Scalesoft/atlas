using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110480, "The 3.4 conversion of stored requirement summaries to overviews runs as the job pages.requirements-overview")]
public sealed class RequirementsOverviewMigration() : EmbeddedSchemaMigration("2026110480_requirements_overview_job.sql");
