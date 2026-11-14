using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110550, "Requirement history lines read as nested ordered lists are repaired by the job pages.requirements-overview")]
public sealed class RequirementsHistoryDatesMigration() : EmbeddedSchemaMigration("2026110550_requirements_history_dates.sql");
