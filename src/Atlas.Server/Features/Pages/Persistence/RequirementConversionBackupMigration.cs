using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110700, "The originals of the revisions pages.requirements-headings converts to requirement headings (not queued yet)")]
public sealed class RequirementConversionBackupMigration() : EmbeddedSchemaMigration("2026110700_requirement_conversion_backup.sql");
