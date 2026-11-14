using FluentMigrator;

namespace Atlas.Server.Features.YouTrack.Persistence;

[Migration(2026110196, "YouTrack projects' short names stored for the place lexicon")]
public sealed class YouTrackProjectKeysMigration() : EmbeddedSchemaMigration("2026110196_youtrack_project_keys.sql");
