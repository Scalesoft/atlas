using FluentMigrator;

namespace Atlas.Server.Features.Contacts.Persistence;

[Migration(2026103197, "One contact per block: the split of multi-contact cards as a background run started once")]
public sealed class ContactSplitRunMigration() : EmbeddedSchemaMigration("2026103197_contact_split_run.sql");

[Migration(2026110171, "The contact runs' primary keys by name")]
public sealed class ContactRunConstraintsMigration() : EmbeddedSchemaMigration("2026110171_contact_run_constraints.sql");

[Migration(2026110340, "Content migrations: multi-contact cards split where read; the background split run goes")]
public sealed class ContactSplitRunDropMigration() : EmbeddedSchemaMigration("2026110340_drop_contact_split_run.sql");
