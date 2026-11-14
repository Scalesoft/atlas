using FluentMigrator;

namespace Atlas.Server.Features.PersonalLibrary.Persistence;

[Migration(18, "Personal favorites, recent visits and draft participation")]
public sealed class PersonalLibraryMigration() : EmbeddedSchemaMigration("018_personal_library.sql");
