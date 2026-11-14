using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Auth;

/// <summary>"Přesouvat stránky tažením" (Atlas 3.6, migration 2026111036): on by default, stored with the account's appearance.</summary>
public sealed class AppearanceDragPagesTests
{
    [PostgreSqlFact]
    public async Task Turning_dragging_off_is_saved_with_the_appearance()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_drag_pages_");
        var user = await host.SeedUserAsync("writer");
        host.Actor.Current = user;
        Assert.True(Appearance.Default.DragPages);
        var off = Appearance.Default with { DragPages = false };
        Assert.Equal(off, await host.InvokeAsync<Appearance>(new UpdateAppearance(off)));
        using var read = host.OpenSession();
        var current = await read.QueryOver<UserAppearancePreference>().Where(p => p.UserId == user.Id && p.ActiveTo == null).SingleOrDefaultAsync();
        Assert.Equal(off, current.Appearance);
    }

    [PostgreSqlFact]
    public async Task The_migration_keeps_dragging_on_for_existing_preferences()
    {
        const string User = "00000000-0000-0000-0000-000000000001";
        await using var schema = await DisposableSchema.CreateAsync("atlas_drag_pages_migration_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026111036);
        await ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{{User}}','writer','Writer',false,true,'stamp');
            INSERT INTO "UserAppearancePreference"("Id","ActiveFrom","UserId","Layout","Accent","Mode","Motion","OpenLocked") VALUES(gen_random_uuid(),now(),'{{User}}','islands','blue','dark','reduce',true);
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT "DragPages","OpenLocked","Layout" FROM "UserAppearancePreference" """, connection);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal((true, true, "islands"), (rows.GetBoolean(0), rows.GetBoolean(1), rows.GetString(2)));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
