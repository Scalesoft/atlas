using Atlas.Server.Platform.Persistence;
using FluentMigrator;

namespace Atlas.Server.Platform.Logs;

[Migration(2026110667, "3.6: the server's own log (Systémový log)")]
public sealed class AppLogMigration() : EmbeddedSchemaMigration("2026110667_app_log.sql");
