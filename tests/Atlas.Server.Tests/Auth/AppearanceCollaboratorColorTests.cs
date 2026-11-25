using Atlas.Markdown.Sync;
using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Auth;

/// <summary>„Moje barva“ (Atlas 5.2, migration 2026112410): the colour of a person in live pages and boards, stored with the account's appearance; automatic by default.</summary>
public sealed class AppearanceCollaboratorColorTests
{
    [PostgreSqlFact]
    public async Task A_chosen_colour_is_saved_with_the_appearance_read_as_the_palette_colour_and_an_unknown_one_is_refused()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_appearance_color_");
        var user = await host.SeedUserAsync("writer");
        host.Actor.Current = user;
        Assert.Equal("auto", Appearance.Default.CollaboratorColor);
        using (var read = host.OpenSession())
        {
            Assert.Null(await CollaboratorColorPreference.ReadAsync(read, user.Id, CancellationToken.None));
        }
        var chosen = Appearance.Default with { CollaboratorColor = "violet" };
        Assert.Equal(chosen, await host.InvokeAsync<Appearance>(new UpdateAppearance(chosen)));
        using (var read = host.OpenSession())
        {
            var current = await read.QueryOver<UserAppearancePreference>().Where(p => p.UserId == user.Id && p.ActiveTo == null).SingleOrDefaultAsync();
            Assert.Equal(chosen, current.Appearance);
            Assert.Equal(CollaboratorPalette.Entries[CollaboratorPalette.SlotOf("violet")].Light, await CollaboratorColorPreference.ReadAsync(read, user.Id, CancellationToken.None));
            Assert.Equal(new Dictionary<Guid, string> { [user.Id] = CollaboratorPalette.Entries[CollaboratorPalette.SlotOf("violet")].Light },
                await CollaboratorColorPreference.ReadAllAsync(read, CancellationToken.None));   // the static surfaces' map, "auto" accounts left out
        }
        // A deactivated account's colour is nobody's business any more.
        await host.ExecuteAsync("""UPDATE "UserAccount" SET "IsActive"=false WHERE "Id"=:id""", user.Id);
        using (var read = host.OpenSession())
        {
            Assert.Empty(await CollaboratorColorPreference.ReadAllAsync(read, CancellationToken.None));
        }
        var refused = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<Appearance>(new UpdateAppearance(Appearance.Default with { CollaboratorColor = "#ff0000" })));
        Assert.Equal(400, refused.Status);
    }

    [PostgreSqlFact]
    public async Task The_migration_leaves_every_existing_preference_automatic()
    {
        const string User = "00000000-0000-0000-0000-000000000001";
        await using var schema = await DisposableSchema.CreateAsync("atlas_appearance_color_migration_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026112410);
        await ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{{User}}','writer','Writer',false,true,'stamp');
            INSERT INTO "UserAppearancePreference"("Id","ActiveFrom","ActiveTo","UserId","Layout","Accent","Mode","Motion","OpenLocked","DragPages","Language")
                VALUES(gen_random_uuid(),now() - interval '2 days',now() - interval '1 day','{{User}}','connected','ocean','system','system',false,true,'auto'),
                      (gen_random_uuid(),now() - interval '1 day',null,'{{User}}','islands','blue','dark','reduce',true,false,'cs');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT "CollaboratorColor","Language","Layout" FROM "UserAppearancePreference" ORDER BY "ActiveFrom" """, connection);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(("auto", "auto", "connected"), (rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        Assert.True(await rows.ReadAsync());
        Assert.Equal(("auto", "cs", "islands"), (rows.GetString(0), rows.GetString(1), rows.GetString(2)));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
