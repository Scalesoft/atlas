using FluentMigrator;

namespace Atlas.Server.Features.Contacts.Persistence;

[Migration(2026103186, "A contact link run also ends as failed or cancelled")]
public sealed class ContactLinkRunEndStatesMigration() : EmbeddedSchemaMigration("2026103186_contact_link_run_end_states.sql");
