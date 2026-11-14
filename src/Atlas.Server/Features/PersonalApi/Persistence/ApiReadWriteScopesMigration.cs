using FluentMigrator;

namespace Atlas.Server.Features.PersonalApi.Persistence;

/// <summary>3.6: token scopes and account levels become read and write; draft, publish and direct all map to write (see the SQL).</summary>
[Migration(2026110684, "3.6: personal API scopes and levels only read and write (draft, publish, direct → write)")]
public sealed class ApiReadWriteScopesMigration() : EmbeddedSchemaMigration("2026110684_api_read_write_scopes.sql");
