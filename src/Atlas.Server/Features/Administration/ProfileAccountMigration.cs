using FluentMigrator;

namespace Atlas.Server.Features.Administration;

[Migration(25, "Self-service account contact and sign-in facts")]
public sealed class ProfileAccountMigration() : EmbeddedSchemaMigration("025_profile_account.sql");
