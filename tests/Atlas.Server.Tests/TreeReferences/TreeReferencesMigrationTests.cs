using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.TreeReferences;

/// <summary>Migration 2026102850 over a wiki that already has spaces, pages and boards: nothing of theirs changes, the new table takes
/// nodes under the existing pages, and placing or removing one moves the space's tree version as pages and boards do.</summary>
public sealed class TreeReferencesMigrationTests
{
    private const long Version = 2026102850;

    [PostgreSqlFact]
    public async Task Existing_trees_stay_and_a_reference_moves_the_tree_version()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_tree_references_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, Version);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-0000000000f1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            """);
        var before = await ScalarAsync(schema.ConnectionString, """SELECT "Version" FROM "SpaceOrganization" WHERE "SpaceId"='00000000-0000-0000-0000-00000000000a'""");

        // Up to 2026110930, which moved what the triggers tested here did to C# (ContentRowEffects).
        await Events.OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110930, from: Version);

        Assert.Equal(before, await ScalarAsync(schema.ConnectionString, """SELECT "Version" FROM "SpaceOrganization" WHERE "SpaceId"='00000000-0000-0000-0000-00000000000a'"""));
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "TreeReference"("Id","SpaceId","ParentPageId","SortOrder","TargetId","Anchor","CreatedBy","CreatedAt")
              VALUES('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-0000000000f1',1024,
                     '00000000-0000-0000-0000-0000000000f1','uvod','00000000-0000-0000-0000-000000000001',now())
            """);
        await ExecuteAsync(schema.ConnectionString, """DELETE FROM "TreeReference" """);
        Assert.Equal((long)before! + 2, await ScalarAsync(schema.ConnectionString, """SELECT "Version" FROM "SpaceOrganization" WHERE "SpaceId"='00000000-0000-0000-0000-00000000000a'"""));
        // A trashed node carries its whole trash stamp, as a board.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "TreeReference"("Id","SpaceId","SortOrder","TargetId","CreatedBy","CreatedAt","IsTrashed")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-00000000000a',1024,gen_random_uuid(),'00000000-0000-0000-0000-000000000001',now(),true)
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
