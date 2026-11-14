using FluentMigrator;

namespace Atlas.Server.Features.ModuleSettings;

[Migration(2026100450, "Wiki-level switch of editor suggestions")]
public sealed class EditorAssistMigration() : EmbeddedSchemaMigration("2026100450_editor_assist_setting.sql");
