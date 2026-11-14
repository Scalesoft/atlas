using FluentMigrator;

namespace Atlas.Server.Features.Hub.Persistence;

[Migration(2026100103, "Distributed Hub sync scheduling")]
public sealed class HubSchedulingMigration() : EmbeddedSchemaMigration("2026100103_hub_scheduling.sql");
