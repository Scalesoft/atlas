using FluentMigrator;

namespace Atlas.Server.Features.Contacts.Persistence;

[Migration(2026110190, "Every account has its directory contact: the suggested pairs a wiki admin decides")]
public sealed class AccountContactSuggestionMigration() : EmbeddedSchemaMigration("2026110190_account_contact_suggestion.sql");

[Migration(2026110330, "Owner decision: the account-contact pairs are computed when read; the stored suggestions go")]
public sealed class AccountContactSuggestionDropMigration() : EmbeddedSchemaMigration("2026110330_drop_account_contact_suggestion.sql");
