using FluentMigrator;

namespace Atlas.Server.Features.PageOrganization.Persistence;

[Migration(2026111033, "Řadit abecedně: a tree level pinned to its titles, on the parent page or the space root")]
public sealed class TreeTitleOrderMigration() : EmbeddedSchemaMigration("2026111033_tree_title_order.sql");
