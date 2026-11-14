using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110630, "Versions of the page history: autosave checkpoints grouped, with their change summary")]
public sealed class RevisionGroupsMigration() : EmbeddedSchemaMigration("2026110630_page_revision_groups.sql");
