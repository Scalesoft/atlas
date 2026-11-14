using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Comments;

/// <summary>Migration 2026102790 over the rows an installation already has: every space keeps comments on (versioned with its
/// settings), the comment tables arrive empty and hold a deleted comment to an empty text.</summary>
public sealed class CommentsMigrationTests
{
    private const long CommentsVersion = 2026102790;

    [PostgreSqlFact]
    public async Task Existing_spaces_get_comments_on_and_the_comment_tables_keep_their_rules()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_comments_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, CommentsVersion);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','One',2,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024);
            """);

        // Up to 2026110930, which moved what the triggers tested here did to C# (ContentRowEffects).
        await Events.OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110930, from: CommentsVersion);

        Assert.Equal("t|t|1", await ScalarAsync(schema.ConnectionString, """
            SELECT s."CommentsEnabled"::text::char || '|' || r."CommentsEnabled"::text::char || '|' || (SELECT count(*) FROM "SpaceMetadataRevision")
            FROM "Space" s JOIN "SpaceMetadataRevision" r ON r."SpaceId"=s."Id" AND r."ActiveTo" IS NULL
            """));
        // Switching comments off is a settings change: a new revision that remembers it.
        await ExecuteAsync(schema.ConnectionString, """UPDATE "Space" SET "CommentsEnabled"=false""");
        Assert.Equal("f|2", await ScalarAsync(schema.ConnectionString, """
            SELECT r."CommentsEnabled"::text::char || '|' || (SELECT count(*) FROM "SpaceMetadataRevision")
            FROM "SpaceMetadataRevision" r WHERE r."ActiveTo" IS NULL
            """));

        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "Comment"("Id","ObjectId","AuthorId","Body","Anchor","CreatedAt")
              VALUES('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-000000000001','Hi','x',now());
            INSERT INTO "CommentMention"("CommentId","UserId") VALUES('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-000000000001');
            """);
        // A deleted comment has no text and no place, and someone who deleted it.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            """UPDATE "Comment" SET "DeletedAt"=now(), "DeletedById"='00000000-0000-0000-0000-000000000001'"""));
        await ExecuteAsync(schema.ConnectionString,
            """UPDATE "Comment" SET "DeletedAt"=now(), "DeletedById"='00000000-0000-0000-0000-000000000001', "Body"='', "Anchor"=NULL""");
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """UPDATE "Comment" SET "DeletedAt"=NULL"""));
        // Purging the object takes its comments with it.
        await ExecuteAsync(schema.ConnectionString, """DELETE FROM "Board" """);
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "Comment" """));
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
