using FluentMigrator;

namespace Atlas.Server.Features.PersonalLibrary.Persistence;

[Migration(2026102650, "Favorites and recent visits of any content resource")]
public sealed class LibraryResourcesMigration() : EmbeddedSchemaMigration("2026102650_library_resources.sql");
