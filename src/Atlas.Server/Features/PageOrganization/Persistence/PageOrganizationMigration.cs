using FluentMigrator;

namespace Atlas.Server.Features.PageOrganization.Persistence;

[Migration(11, "Page organization and trash history")]
public sealed class PageOrganizationMigration() : EmbeddedSchemaMigration("011_page_organization.sql");
