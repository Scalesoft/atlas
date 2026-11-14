using FluentMigrator;

namespace Atlas.Server.Features.PersonalApi.Persistence;

[Migration(2026100600, "Personal API token rights: levels, scopes read/draft/publish, channels rest/mcp")]
public sealed class ApiTokenRightsMigration() : EmbeddedSchemaMigration("2026100600_api_token_rights.sql");
