using FluentMigrator;

namespace Atlas.Server.Features.Contacts.Persistence;

[Migration(2026103190, "The merged contacts are read by a partial index")]
public sealed class ContactMergedIntoIndexMigration() : EmbeddedSchemaMigration("2026103190_contact_merged_into_index.sql");
