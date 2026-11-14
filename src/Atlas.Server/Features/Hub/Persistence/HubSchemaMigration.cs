using FluentMigrator;

namespace Atlas.Server.Features.Hub.Persistence;

[Migration(2026100101, "Hub group bindings and expiring membership")]
public sealed class HubSchemaMigration() : EmbeddedSchemaMigration("2026100101_hub_groups.sql");
