using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Objects;

/// <summary>Migration 2026102910 over the envelopes an installation already has: live and trashed objects, a board's, keep every value; who
/// trashed an object before is unknown (NULL), and the purge audit arrives empty.</summary>
public sealed class ObjectTrashMigrationTests
{
    private const long Version = 2026102910;

    [PostgreSqlFact]
    public async Task Existing_envelopes_stay_as_they_were_and_nobody_is_named_for_an_earlier_trash()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_object_trash_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, Version);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','One',2,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024);
            INSERT INTO "ContentResource"("Id","TypeId","Scope","Version","CreatedAt","CreatedBy","UpdatedAt","TrashedAt","Title","Data") VALUES
              ('00000000-0000-0000-0000-0000000000c1','atlas.contact','wiki',1,now(),'00000000-0000-0000-0000-000000000001',now(),NULL,'Jana','{"name":"Jana"}'),
              ('00000000-0000-0000-0000-0000000000c2','atlas.contact','wiki',3,now(),'00000000-0000-0000-0000-000000000001',now(),'2026-10-28T10:00:00Z','Karel','{"name":"Karel"}');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        // The board's envelope carries its title since 2026102930 (personal items), which migrating to the head applies too.
        Assert.Equal("b1=One,2,live,-|c1=Jana,1,live,-|c2=Karel,3,trashed,-", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(right("Id"::text,2) || '=' || coalesce("Title","TypeId") || ',' || "Version" || ',' ||
              CASE WHEN "TrashedAt" IS NULL THEN 'live' ELSE 'trashed' END || ',' || coalesce("TrashedById"::text,'-'), '|' ORDER BY "Id")
            FROM "ContentResource"
            """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "ObjectPurgeEvent" """));
        // A new trash names its author, who must be an account.
        await ExecuteAsync(schema.ConnectionString, """
            UPDATE "ContentResource" SET "TrashedAt"=now(), "TrashedById"='00000000-0000-0000-0000-000000000001' WHERE "Id"='00000000-0000-0000-0000-0000000000c1'
            """);
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """
            UPDATE "ContentResource" SET "TrashedById"='00000000-0000-0000-0000-000000000009' WHERE "Id"='00000000-0000-0000-0000-0000000000c2'
            """));
    }

    /// <summary>Applies the embedded scripts below a version, with their journal, as the previous release left the schema.</summary>
    private static async Task ApplyBeforeAsync(string connectionString, long version)
    {
        await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" bigint PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < version).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
            await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}
