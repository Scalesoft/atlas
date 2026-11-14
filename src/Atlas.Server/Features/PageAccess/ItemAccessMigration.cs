using FluentMigrator;

namespace Atlas.Server.Features.PageAccess;

[Migration(2026092601, "Item access foundation")]
public sealed class ItemAccessMigration() : EmbeddedSchemaMigration("2026092601_item_access.sql");
