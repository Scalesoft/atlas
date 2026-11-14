using FluentMigrator;

namespace Atlas.Server.Features.Administration;

[Migration(2026092831, "Invitation and password reset codes with their own lifetimes")]
public sealed class AccountTokenPurposeMigration() : EmbeddedSchemaMigration("2026092831_account_token_purpose.sql");
