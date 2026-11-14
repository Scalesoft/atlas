using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026111036, "The profile choice to move pages in the tree by dragging")]
public sealed class AppearanceDragPagesMigration() : EmbeddedSchemaMigration("2026111036_appearance_drag_pages.sql");
