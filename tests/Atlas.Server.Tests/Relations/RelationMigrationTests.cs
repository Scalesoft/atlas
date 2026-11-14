using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Relations;

/// <summary>Migrations 2026102830 and 2026102840 over the rows an installation already has: the placement index over existing cards,
/// and the mention projection backfilled from published revisions (objectId, else one unambiguous e-mail match in the wiki or the
/// page's space), then kept by the publication trigger.</summary>
public sealed class RelationMigrationTests
{
    private const string Seed = """
        INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
          VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
        INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue'),
          ('00000000-0000-0000-0000-00000000000b','Second','book-open','blue');
        INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
          VALUES('00000000-0000-0000-0000-0000000000f1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',2);
        """;

    [PostgreSqlFact]
    public async Task Existing_cards_are_found_by_their_target_through_the_new_index()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_board_target_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026102830);
        await ExecuteAsync(schema.ConnectionString, Seed + """
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','One',2,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","TargetResourceId","Version","IsDeleted") VALUES
              ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b1','page',0,0,240,160,0,'00000000-0000-0000-0000-0000000000f1',1,false),
              ('00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000b1','page',0,0,240,160,0,'00000000-0000-0000-0000-0000000000f1',2,true),
              ('00000000-0000-0000-0000-0000000000c3','00000000-0000-0000-0000-0000000000b1','note',0,0,160,120,0,NULL,1,false);
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        const string live = """
            SELECT count(*) FROM "BoardItem" WHERE "TargetResourceId"='00000000-0000-0000-0000-0000000000f1' AND NOT "IsDeleted" AND "TargetResourceId" IS NOT NULL
            """;
        Assert.Equal(1L, await ScalarAsync(schema.ConnectionString, live));
        Assert.Contains("IX_BoardItem_Target", (string)(await ScalarAsync(schema.ConnectionString, "SET enable_seqscan=off; EXPLAIN " + live))!);
        const string space = """
            SELECT count(*) FROM "ContentResource" WHERE "SpaceId"='00000000-0000-0000-0000-00000000000a' AND "TrashedAt" IS NULL
            """;
        Assert.Equal(2L, await ScalarAsync(schema.ConnectionString, space));
        Assert.Contains("IX_ContentResource_Space", (string)(await ScalarAsync(schema.ConnectionString, "SET enable_seqscan=off; EXPLAIN " + space))!);
    }

    [PostgreSqlFact]
    public async Task Published_contact_snapshots_become_mentions_by_object_id_or_one_unambiguous_e_mail()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_page_mentions_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026102840);
        var named = Guid.NewGuid();
        // Wiki a@x, space b@x (and the other space's b@x, which does not count), two wiki contacts sharing d@x, a trashed e@x.
        await ExecuteAsync(schema.ConnectionString, Seed + $$"""
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","CreatedAt","Title","Data","TrashedAt") VALUES
              ('00000000-0000-0000-0000-0000000000a1','atlas.contact','wiki',NULL,now(),'A','{"name":"A","emails":["a@x.cz"]}',NULL),
              ('00000000-0000-0000-0000-0000000000a2','atlas.contact','space','00000000-0000-0000-0000-00000000000a',now(),'B','{"name":"B","emails":["b@x.cz"]}',NULL),
              ('00000000-0000-0000-0000-0000000000a3','atlas.contact','space','00000000-0000-0000-0000-00000000000b',now(),'B2','{"name":"B2","emails":["b@x.cz"]}',NULL),
              ('00000000-0000-0000-0000-0000000000a4','atlas.contact','wiki',NULL,now(),'D1','{"name":"D1","emails":["d@x.cz"]}',NULL),
              ('00000000-0000-0000-0000-0000000000a5','atlas.contact','wiki',NULL,now(),'D2','{"name":"D2","emails":["d@x.cz"]}',NULL),
              ('00000000-0000-0000-0000-0000000000a6','atlas.contact','wiki',NULL,now(),'E','{"name":"E","emails":["e@x.cz"]}',now());
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e1','00000000-0000-0000-0000-0000000000f1',1,'P','{{Doc(Inline(Contact(objectId: named.ToString().ToUpperInvariant())),
                  Card(Contact("A@X.cz"), Contact("b@x.cz"), Contact("d@x.cz"), Contact("e@x.cz"), Contact("nobody@x.cz")))}}','',
               '00000000-0000-0000-0000-000000000001',now()-interval '1 day',now(),'web'),
              ('00000000-0000-0000-0000-0000000000e2','00000000-0000-0000-0000-0000000000f1',2,'P','{{Doc(Card(Contact("a@x.cz")))}}','',
               '00000000-0000-0000-0000-000000000001',now(),NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e1';
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(string.Join("|", new[] { "00000000-0000-0000-0000-0000000000a1", "00000000-0000-0000-0000-0000000000a2", named.ToString() }.Order()),
            await MentionsAsync(schema.ConnectionString));
        // Since migration 2026110872 code derives them (PageDerivedRowsTests): the rows stay and the rebuild job is queued.
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'TR_Page%ObjectMention%'"""));
    }

    [PostgreSqlFact]
    public async Task Working_revisions_of_existing_pages_join_the_mention_projection_and_follow_each_save()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_page_mentions_drafts_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026103140);
        // A published revision mentioning a1 and a later working revision mentioning a2, as 3.0 left them: only a1 was projected.
        await ExecuteAsync(schema.ConnectionString, Seed + $$"""
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","CreatedAt","Title","Data") VALUES
              ('00000000-0000-0000-0000-0000000000a1','atlas.contact','wiki',NULL,now(),'A','{"name":"A"}'),
              ('00000000-0000-0000-0000-0000000000a2','atlas.contact','wiki',NULL,now(),'B','{"name":"B"}'),
              ('00000000-0000-0000-0000-0000000000a3','atlas.contact','wiki',NULL,now(),'C','{"name":"C"}');
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e1','00000000-0000-0000-0000-0000000000f1',1,'P','{{Doc(Card(Contact(objectId: "00000000-0000-0000-0000-0000000000a1")))}}','',
               '00000000-0000-0000-0000-000000000001',now()-interval '1 day',now(),'web'),
              ('00000000-0000-0000-0000-0000000000e2','00000000-0000-0000-0000-0000000000f1',2,'P','{{Doc(Card(Contact(objectId: "00000000-0000-0000-0000-0000000000a2")))}}','',
               '00000000-0000-0000-0000-000000000001',now(),NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e1';
            """);
        Assert.Equal("e1:a1", await RevisionMentionsAsync(schema.ConnectionString));

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("e1:a1|e2:a2", await RevisionMentionsAsync(schema.ConnectionString));
        Assert.Equal("00000000-0000-0000-0000-0000000000a1", await MentionsAsync(schema.ConnectionString));
    }

    /// <summary>Every row as revision:contact, by the last two characters of each id.</summary>
    private static async Task<string> RevisionMentionsAsync(string connectionString) => (string)(await ScalarAsync(connectionString, """
        SELECT coalesce(string_agg(right(m."SourceRevisionId"::text,2) || ':' || right(m."ObjectId"::text,2),'|' ORDER BY m."SourceRevisionId",m."ObjectId"),'')
        FROM "PageObjectMention" m
        """))!;

    [PostgreSqlFact]
    public async Task Mentions_name_contacts_only_and_follow_a_page_into_another_space()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_page_mentions_space_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026102860);
        var missing = Guid.NewGuid();
        // An objectId naming a page (no contact) and one naming nothing (a purged contact: broken); b@x in each space.
        await ExecuteAsync(schema.ConnectionString, Seed + $$"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-0000000000f2','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","CreatedAt","Title","Data","TrashedAt") VALUES
              ('00000000-0000-0000-0000-0000000000a2','atlas.contact','space','00000000-0000-0000-0000-00000000000a',now(),'B','{"name":"B","emails":["b@x.cz"]}',NULL),
              ('00000000-0000-0000-0000-0000000000a3','atlas.contact','space','00000000-0000-0000-0000-00000000000b',now(),'B2','{"name":"B2","emails":["b@x.cz"]}',NULL);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e1','00000000-0000-0000-0000-0000000000f1',1,'P','{{Doc(Card(Contact(objectId: "00000000-0000-0000-0000-0000000000f2"),
                  Contact(objectId: missing.ToString()), Contact("b@x.cz")))}}','','00000000-0000-0000-0000-000000000001',now(),NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e1' WHERE "Id"='00000000-0000-0000-0000-0000000000f1';
            """);
        // Before the migration a move kept the first space's match, and the page's id counted as a contact.
        await ExecuteAsync(schema.ConnectionString, """UPDATE "Page" SET "SpaceId"='00000000-0000-0000-0000-00000000000b' WHERE "Id"='00000000-0000-0000-0000-0000000000f1'""");
        Assert.Contains("00000000-0000-0000-0000-0000000000f2", await MentionsAsync(schema.ConnectionString));

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(string.Join("|", new[] { "00000000-0000-0000-0000-0000000000a3", missing.ToString() }.Order()), await MentionsAsync(schema.ConnectionString));
    }

    private static string Contact(string? email = null, string? objectId = null) =>
        "{\"name\":\"X\"" + (email is null ? "" : $",\"emails\":[\"{email}\"]") + (objectId is null ? "" : $",\"objectId\":\"{objectId}\"") + "}";

    private static string Card(params string[] contacts) => Node("contactCard", contacts);

    private static string Inline(params string[] contacts) => "{\"type\":\"paragraph\",\"content\":[" + Node("contactInline", contacts) + "]}";

    private static string Node(string type, string[] contacts) =>
        "{\"type\":\"" + type + "\",\"attrs\":{\"data\":{\"version\":1,\"contacts\":[" + string.Join(",", contacts) + "]}}}";

    private static string Doc(params string[] blocks) =>
        "{\"format\":\"scalesoft-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[" + string.Join(",", blocks) + "]}}";

    private static async Task<string> MentionsAsync(string connectionString) => (string)(await ScalarAsync(connectionString, """
        SELECT coalesce(string_agg(m."ObjectId"::text,'|' ORDER BY m."ObjectId"::text),'') FROM "PageObjectMention" m
        JOIN "Page" p ON p."Id"=m."SourcePageId" AND p."PublishedRevisionId"=m."SourceRevisionId"
        """))!;

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
        await using var reader = await command.ExecuteReaderAsync();
        // EXPLAIN answers one row per plan line; the whole plan is what a caller looks for.
        var lines = new List<object>();
        do
        {
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetValue(0));
            }
        } while (await reader.NextResultAsync());
        return lines.Count switch { 0 => null, 1 => lines[0], _ => string.Join("\n", lines) };
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}
