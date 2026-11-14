using Npgsql;
using Atlas.Server.Features.Spaces.Commands;
using Atlas.Server.Features.Spaces.Contracts;
using Atlas.Server.Features.Spaces.Queries;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.SpaceSettings;
using Xunit;

namespace Atlas.Server.Tests.Spaces;

/// <summary>Who may use the space trash, and migration 2026102710 over existing spaces.</summary>
public sealed class SpaceTrashTests
{
    [PostgreSqlFact]
    public async Task Only_a_wiki_admin_trashes_lists_and_restores_a_space()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        // The space Admin manages the space but not its existence, as creating it is the wiki admin's too.
        await Denied(403, () => Run(f, f.SpaceAdmin, new TrashSpace(f.SpaceId, new("Original"))));
        await Denied(403, () => f.As<IReadOnlyList<TrashedSpaceItem>>(f.SpaceAdmin, new ListSpaceTrash()));
        await Run(f, f.Admin, new TrashSpace(f.SpaceId, new("Original")));
        await Denied(403, () => Run(f, f.SpaceAdmin, new RestoreSpace(f.SpaceId)));
        // The space trash lists every test's trashed spaces: this test's own by its id.
        var item = Assert.Single(await f.As<IReadOnlyList<TrashedSpaceItem>>(f.Admin, new ListSpaceTrash()), x => x.Id == f.SpaceId);
        Assert.Equal((f.SpaceId, f.Admin.Id, 0), (item.Id, item.TrashedById, item.PageCount));
        await Run(f, f.Admin, new RestoreSpace(f.SpaceId));
        Assert.DoesNotContain(await f.As<IReadOnlyList<TrashedSpaceItem>>(f.Admin, new ListSpaceTrash()), x => x.Id == f.SpaceId);
    }

    [PostgreSqlFact]
    public async Task Existing_spaces_stay_live_and_a_half_trashed_row_is_refused()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_space_trash_migration_");
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        await ExecuteAsync(schema.ConnectionString, """
            ALTER TABLE "Space" DROP CONSTRAINT "CK_Space_Trash", DROP COLUMN "TrashedAt", DROP COLUMN "TrashedById", DROP COLUMN "TrashBatchId";
            DELETE FROM "SchemaVersion" WHERE "Version" = 2026102710;
            INSERT INTO "Space"("Id","Name","Icon","Color","Description","PinPolicy") VALUES (gen_random_uuid(),'Provoz','book-open','blue','','None');
            -- The migration also creates a search function over a helper 2026110935 dropped; a stub lets it run again here.
            CREATE FUNCTION atlas_normalize_search_text(value text) RETURNS text LANGUAGE sql AS $$ SELECT value $$;
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using (var live = new NpgsqlCommand("""SELECT count(*) FROM "Space" WHERE "Name"='Provoz' AND "TrashedAt" IS NULL AND "TrashBatchId" IS NULL""", connection))
        {
            Assert.Equal(1L, await live.ExecuteScalarAsync());
        }
        await using var half = new NpgsqlCommand("""UPDATE "Space" SET "TrashedAt"=now() WHERE "Name"='Provoz'""", connection);
        await Assert.ThrowsAsync<PostgresException>(() => half.ExecuteNonQueryAsync());
    }

    private static Task Run(SpaceSettingsFixture f, UserAccount actor, object message)
    {
        f.Host.Actor.Current = actor;
        return f.Host.InvokeAsync(message);
    }

    private static async Task Denied(int status, Func<Task> action) =>
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
