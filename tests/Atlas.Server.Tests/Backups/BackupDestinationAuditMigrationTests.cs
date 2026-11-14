using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Events.OutboxTests;

namespace Atlas.Server.Tests.Backups;

/// <summary>Migration 2026102996 over an existing access audit: its events keep satisfying the rewritten constraints, and a backup
/// destination change afterwards is one more event of its own action.</summary>
public sealed class BackupDestinationAuditMigrationTests
{
    [PostgreSqlFact]
    public async Task Existing_audit_events_stay_valid_and_a_destination_change_is_audited()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_backup_destination_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026102996);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            """);
        var before = await ScalarAsync(schema.ConnectionString, """SELECT string_agg("Action", ',' ORDER BY "Id") FROM "AccessAuditEvent" """);
        Assert.NotNull(before);

        // Up to the migrations that move this audit into the code (2026110944, tested in AuditInCodeMigrationTests).
        await ApplyBeforeAsync(schema.ConnectionString, 2026110941, from: 2026102996);

        Assert.Equal(before, await ScalarAsync(schema.ConnectionString, """SELECT string_agg("Action", ',' ORDER BY "Id") FROM "AccessAuditEvent" """));
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "BackupDestinationChange"("ActorId","Kind","Destination") VALUES('00000000-0000-0000-0000-000000000001','git.remote','git@example.test:a.git');
            """);
        Assert.Equal("backup.destination_changed|00000000-0000-0000-0000-000000000001", await ScalarAsync(schema.ConnectionString, """
            SELECT e."Action" || '|' || e."ActorId" FROM "AccessAuditEvent" e WHERE e."BackupDestinationChangeId" IS NOT NULL
            """));
        await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => ExecuteAsync(schema.ConnectionString, """DELETE FROM "BackupDestinationChange" """));
    }

    /// <summary>Migration 2026103010 over a destination change audited before it: the view now names its kind and destination, and the
    /// new kinds are a mode switch (backup.destination_changed) and a download (backup.downloaded).</summary>
    [PostgreSqlFact]
    public async Task An_earlier_destination_change_names_its_destination_and_a_download_is_its_own_action()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_backup_audit_view_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026103010);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            INSERT INTO "BackupDestinationChange"("ActorId","Kind","Destination") VALUES('00000000-0000-0000-0000-000000000001','git.remote','git@example.test:a.git');
            """);
        await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "BackupDestinationChange"("ActorId","Kind","Destination") VALUES('00000000-0000-0000-0000-000000000001','archive.download','a.zip');
            """));

        await ApplyBeforeAsync(schema.ConnectionString, 2026110941, from: 2026103010);

        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "BackupDestinationChange"("ActorId","Kind","Destination") VALUES
              ('00000000-0000-0000-0000-000000000001','archive.complete','/var/backups'),
              ('00000000-0000-0000-0000-000000000001','archive.download','wiki-complete.zip');
            """);
        Assert.Equal("backup.destination_changed|git.remote|git@example.test:a.git;backup.destination_changed|archive.complete|/var/backups;"
            + "backup.downloaded|archive.download|wiki-complete.zip", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg("Action" || '|' || "BackupKind" || '|' || "BackupDestination", ';' ORDER BY "BackupDestinationChangeId") FROM "AccessAuditView"
            WHERE "BackupDestinationChangeId" IS NOT NULL
            """));
    }
}
