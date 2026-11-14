using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Auth;

/// <summary>„Jazyk“ (Atlas 4.1, migration 2026111100): the language of the tool, stored with the account's appearance; auto by default.</summary>
public sealed class AppearanceLanguageTests
{
    [PostgreSqlFact]
    public async Task A_chosen_language_is_saved_with_the_appearance_and_an_unknown_one_is_refused()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_appearance_language_");
        var user = await host.SeedUserAsync("writer");
        host.Actor.Current = user;
        Assert.Equal("auto", Appearance.Default.Language);
        var english = Appearance.Default with { Language = "en" };
        Assert.Equal(english, await host.InvokeAsync<Appearance>(new UpdateAppearance(english)));
        using (var read = host.OpenSession())
        {
            var current = await read.QueryOver<UserAppearancePreference>().Where(p => p.UserId == user.Id && p.ActiveTo == null).SingleOrDefaultAsync();
            Assert.Equal(english, current.Appearance);
        }
        var refused = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<Appearance>(new UpdateAppearance(Appearance.Default with { Language = "de" })));
        Assert.Equal(400, refused.Status);
    }

    [PostgreSqlFact]
    public async Task The_migration_leaves_the_language_of_existing_preferences_to_the_browser()
    {
        const string User = "00000000-0000-0000-0000-000000000001";
        await using var schema = await DisposableSchema.CreateAsync("atlas_appearance_language_migration_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026111100);
        await ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{{User}}','writer','Writer',false,true,'stamp');
            INSERT INTO "UserAppearancePreference"("Id","ActiveFrom","ActiveTo","UserId","Layout","Accent","Mode","Motion","OpenLocked","DragPages")
                VALUES(gen_random_uuid(),now() - interval '2 days',now() - interval '1 day','{{User}}','connected','ocean','system','system',false,true),
                      (gen_random_uuid(),now() - interval '1 day',null,'{{User}}','islands','blue','dark','reduce',true,false);
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT "Language","DragPages","Layout" FROM "UserAppearancePreference" ORDER BY "ActiveFrom" """, connection);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(("auto", true, "connected"), (rows.GetString(0), rows.GetBoolean(1), rows.GetString(2)));
        Assert.True(await rows.ReadAsync());
        Assert.Equal(("auto", false, "islands"), (rows.GetString(0), rows.GetBoolean(1), rows.GetString(2)));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
