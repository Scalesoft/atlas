using FluentMigrator;

namespace Atlas.Server.Features.Hub.Persistence;

[Migration(2026100104, "Hub change provenance in the access audit")]
public sealed class HubAuditMigration() : EmbeddedSchemaMigration("2026100104_hub_audit.sql");
