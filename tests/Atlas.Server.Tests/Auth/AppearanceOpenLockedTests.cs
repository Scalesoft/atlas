using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Auth;

/// <summary>"Otevírat stránky zamčené" (Atlas 3.6, migration 2026110560): stored with the account's appearance, so it follows the user to every device.</summary>
public sealed class AppearanceOpenLockedTests
{
    [PostgreSqlFact]
    public async Task The_choice_is_saved_with_the_appearance_and_survives_a_reload()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_open_locked_");
        var user = await host.SeedUserAsync("reader");
        host.Actor.Current = user;
        await host.SeedAsync(session => session.SaveAsync(new UserAppearancePreference { UserId = user.Id }));
        Assert.False(Appearance.Default.OpenLocked);
        var locked = new Appearance("connected", "ocean", "system", "system", true);
        Assert.Equal(locked, await host.InvokeAsync<Appearance>(new UpdateAppearance(locked)));
        using var read = host.OpenSession();
        var current = await read.QueryOver<UserAppearancePreference>().Where(p => p.UserId == user.Id && p.ActiveTo == null).SingleOrDefaultAsync();
        Assert.Equal(locked, current.Appearance);
    }

    [PostgreSqlFact]
    public async Task The_migration_leaves_existing_preferences_opening_pages_unlocked()
    {
        const string User = "00000000-0000-0000-0000-000000000001";
        await using var schema = await DisposableSchema.CreateAsync("atlas_open_locked_migration_");
        await ExecuteAsync(schema.ConnectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" bigint PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026110560).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await ExecuteAsync(schema.ConnectionString, await reader.ReadToEndAsync());
            await ExecuteAsync(schema.ConnectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        await ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{{User}}','reader','Reader',false,true,'stamp');
            INSERT INTO "UserAppearancePreference"("Id","ActiveFrom","UserId","Layout","Accent","Mode","Motion") VALUES(gen_random_uuid(),now(),'{{User}}','islands','blue','dark','reduce');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT "OpenLocked","Layout","Motion" FROM "UserAppearancePreference" """, connection);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal((false, "islands", "reduce"), (rows.GetBoolean(0), rows.GetString(1), rows.GetString(2)));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
