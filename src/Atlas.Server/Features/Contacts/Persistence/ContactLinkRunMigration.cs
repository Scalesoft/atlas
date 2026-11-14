using FluentMigrator;

namespace Atlas.Server.Features.Contacts.Persistence;

[Migration(2026103185, "The link migration of page contacts as a resumable background run")]
public sealed class ContactLinkRunMigration() : EmbeddedSchemaMigration("2026103185_contact_link_run.sql");

[Migration(2026110450, "The link migration's runs become runs of the job contacts.link")]
public sealed class ContactLinkRunToJobMigration() : EmbeddedSchemaMigration("2026110450_contact_link_run_to_job.sql");
