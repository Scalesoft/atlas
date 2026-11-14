using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Domain;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

public sealed class McpModuleStateTests
{
    [PostgreSqlFact]
    public async Task A_read_started_before_disable_cannot_restore_the_enabled_cache()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_mcp_cache_") /* own database: DDL (renames McpSetting, the installation's one module switch, and puts a view in its place) */;
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        await host.SeedAsync(session => session.SaveAsync(new McpSetting
        {
            Id = Guid.NewGuid(), Enabled = true, ChangedById = admin.Id, ActiveFrom = DateTime.UtcNow.AddSeconds(-1)
        }));
        // Pause the SELECT after PostgreSQL has taken its statement snapshot, without a production test hook.
        await host.SeedAsync(session => session.CreateSQLQuery("""
            ALTER TABLE "McpSetting" RENAME TO "McpSettingRows";
            CREATE FUNCTION hold_mcp(value boolean) RETURNS boolean LANGUAGE plpgsql VOLATILE AS $$
            BEGIN PERFORM pg_advisory_xact_lock(4613001); RETURN value; END $$;
            CREATE VIEW "McpSetting" AS SELECT "Id", hold_mcp("Enabled") AS "Enabled", "ChangedById", "ActiveFrom", "ActiveTo" FROM "McpSettingRows";
            """).ExecuteUpdateAsync());
        var state = new McpModuleState(host.Database, TimeProvider.System);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var blocker = new NpgsqlConnection(host.ConnectionString);
        await blocker.OpenAsync(deadline.Token);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(4613001)", blocker))
            await command.ExecuteNonQueryAsync(deadline.Token);
        var pending = state.IsEnabledAsync(deadline.Token).AsTask();
        try
        {
            await using var observer = new NpgsqlConnection(host.ConnectionString);
            await observer.OpenAsync(deadline.Token);
            await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @blocker = ANY(pg_blocking_pids(pid)))", observer);
            waiting.Parameters.AddWithValue("blocker", blocker.ProcessID);
            while (!Convert.ToBoolean(await waiting.ExecuteScalarAsync(deadline.Token))) await Task.Yield();
            await host.ExecuteAsync("""
                WITH closed AS (
                    UPDATE "McpSettingRows" SET "ActiveTo"=clock_timestamp() WHERE "ActiveTo" IS NULL RETURNING "ActiveTo"
                )
                INSERT INTO "McpSettingRows" ("Id","Enabled","ChangedById","ActiveFrom")
                SELECT gen_random_uuid(),false,:id,"ActiveTo" FROM closed
                """, admin.Id);
            state.Invalidate();
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(4613001)", blocker);
            await release.ExecuteNonQueryAsync();
        }
        Assert.False(await pending);
        Assert.False(await state.IsEnabledAsync(deadline.Token));
    }
}
