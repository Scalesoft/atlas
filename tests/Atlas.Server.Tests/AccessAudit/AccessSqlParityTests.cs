using System.Text.Json.Nodes;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.PersonalItems;
using Atlas.Server.Features.PersonalItems.Commands;
using Atlas.Server.Features.PersonalItems.Contracts;
using Atlas.Server.Features.PersonalItems.Queries;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Events.OutboxTests;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// AccessSql (C# query text since 3.6) decides exactly as the functions it replaces: over a schema stopped before migration 2026110946,
/// which drops them, every case below gives the function's answer and the text's answer side by side. Then the migration: the functions
/// are gone and no row changed.
/// </summary>
public sealed class AccessSqlParityTests
{
    private const string Wiki = "00000000-0000-0000-0000-000000000001", Writer = "00000000-0000-0000-0000-000000000002";
    private const string Reader = "00000000-0000-0000-0000-000000000003", Member = "00000000-0000-0000-0000-000000000004";
    private const string Inactive = "00000000-0000-0000-0000-000000000005", Expired = "00000000-0000-0000-0000-000000000006";
    private const string Group = "00000000-0000-0000-0000-0000000000b1";
    private const string Spaced = "00000000-0000-0000-0000-0000000000c1", Empty = "00000000-0000-0000-0000-0000000000c2";
    private const string Excluded = "00000000-0000-0000-0000-0000000000c3", Readmitted = "00000000-0000-0000-0000-0000000000c4";
    private const string Page = "00000000-0000-0000-0000-0000000000d1", Template = "00000000-0000-0000-0000-0000000000d2";
    private const string Item = "00000000-0000-0000-0000-0000000000e1", Unknown = "00000000-0000-0000-0000-0000000000ff";
    // Hub authority, grant validity and trash: a Hub-verified WikiAdmin and Hub-bound members (verified and lapsed), a grant that starts
    // later, a closed grant, a closed membership, an inactive group member, and a space whose only page is trashed.
    private const string HubAdmin = "00000000-0000-0000-0000-000000000007", HubMember = "00000000-0000-0000-0000-000000000008";
    private const string HubLapsed = "00000000-0000-0000-0000-000000000009", Future = "00000000-0000-0000-0000-00000000000a";
    private const string Closed = "00000000-0000-0000-0000-00000000000b", Left = "00000000-0000-0000-0000-00000000000c";
    private const string InactiveMember = "00000000-0000-0000-0000-00000000000d";
    private const string AdminGroup = "00000000-0000-0000-0000-0000000000b2", HubGroup = "00000000-0000-0000-0000-0000000000b3";
    private const string Connection = "00000000-0000-0000-0000-0000000000a1", Trashy = "00000000-0000-0000-0000-0000000000c5";
    private const string Trashed = "00000000-0000-0000-0000-0000000000d3";

    [PostgreSqlFact]
    public async Task Access_predicates_in_CSharp_answer_as_their_functions_and_migration_2026110946_drops_them()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_access_sql_parity_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110946);
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('{Wiki}','wiki','Wiki',true,true,'s'),('{Writer}','writer','Writer',false,true,'s'),('{Reader}','reader','Reader',false,true,'s'),
              ('{Member}','member','Member',false,true,'s'),('{Inactive}','inactive','Inactive',false,false,'s'),('{Expired}','expired','Expired',false,true,'s');
            INSERT INTO "UserGroup"("Id","Name") VALUES('{Group}','Tým');
            INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom") VALUES(gen_random_uuid(),'{Group}','{Member}',now()-interval '1 day');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Spaced}','A','book','blue'),('{Empty}','B','book','blue'),
              ('{Excluded}','C','book','blue'),('{Readmitted}','D','book','blue');
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ActiveFrom") VALUES(gen_random_uuid(),'{Spaced}','{Writer}',2,now()-interval '1 day'),
              (gen_random_uuid(),'{Spaced}','{Reader}',1,now()-interval '1 day'),(gen_random_uuid(),'{Empty}','{Writer}',2,now()-interval '1 day'),
              (gen_random_uuid(),'{Spaced}','{Inactive}',2,now()-interval '1 day');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','{Spaced}','{Writer}',1);
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","IsTemplate") VALUES('{Template}','{Empty}','{Writer}',1,true);
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(),'{Excluded}',false,'{Wiki}',now()-interval '1 day',NULL),
              (gen_random_uuid(),'{Readmitted}',false,'{Wiki}',now()-interval '2 days',now()-interval '1 day'),
              (gen_random_uuid(),'{Readmitted}',true,'{Wiki}',now()-interval '1 day',NULL);
            INSERT INTO "ContentResource"("Id","TypeId","Scope","CreatedAt","Title","Data") VALUES('{Item}','atlas.contact','wiki',now(),'Jana','{"{"}{"}"}');
            INSERT INTO "ResourceGrant"("Id","ObjectId","UserId","GroupId","Role","ExpiresAt") VALUES
              (gen_random_uuid(),'{Item}','{Reader}',NULL,1,NULL),(gen_random_uuid(),'{Item}',NULL,'{Group}',2,NULL),
              (gen_random_uuid(),'{Item}','{Expired}',NULL,2,now()-interval '1 hour'),(gen_random_uuid(),'{Item}','{Inactive}',NULL,2,NULL);

            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('{HubAdmin}','hub-admin','Hub admin',false,true,'s'),('{HubMember}','hub-member','Hub member',false,true,'s'),
              ('{HubLapsed}','hub-lapsed','Hub lapsed',false,true,'s'),('{Future}','future','Future',false,true,'s'),
              ('{Closed}','closed','Closed',false,true,'s'),('{Left}','left','Left',false,true,'s'),
              ('{InactiveMember}','inactive-member','Inactive member',false,false,'s');
            INSERT INTO "UserGroup"("Id","Name") VALUES('{AdminGroup}','Hub admins'),('{HubGroup}','Hub team');
            INSERT INTO "HubConnection"("Id") VALUES('{Connection}');
            INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
              VALUES(gen_random_uuid(),'{Connection}','https://hub.example.test','https://hub.example.test','p',2,'{Wiki}',now()-interval '1 hour');
            INSERT INTO "HubGroupBinding"("GroupId","ConnectionId","ExternalGroupId","GrantsWikiAdmin") VALUES
              ('{AdminGroup}','{Connection}','admins',true),('{HubGroup}','{Connection}','team',false);
            INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom") VALUES
              ('{HubAdmin}','{HubAdmin}','https://hub.example.test','sub-admin',now()-interval '1 hour'),
              ('{HubMember}','{HubMember}','https://hub.example.test','sub-member',now()-interval '1 hour'),
              ('{HubLapsed}','{HubLapsed}','https://hub.example.test','sub-lapsed',now()-interval '1 hour');
            INSERT INTO "HubIdentityState"("ExternalIdentityId","UserId","ConnectionId","ExternalUserId","SnapshotStartedAt","VerifiedUntil","LastSuccessAt","LastAttemptAt","UpstreamAccountState","Generation") VALUES
              ('{HubAdmin}','{HubAdmin}','{Connection}','sub-admin',now(),now()+interval '5 minutes',now(),now(),1,1),
              ('{HubMember}','{HubMember}','{Connection}','sub-member',now(),now()+interval '5 minutes',now(),now(),1,1),
              ('{HubLapsed}','{HubLapsed}','{Connection}','sub-lapsed',now()-interval '20 minutes',now()-interval '16 minutes',now(),now(),1,1);
            INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(),'{AdminGroup}','{HubAdmin}',now()-interval '1 day',NULL),(gen_random_uuid(),'{HubGroup}','{HubMember}',now()-interval '1 day',NULL),
              (gen_random_uuid(),'{HubGroup}','{HubLapsed}',now()-interval '1 day',NULL),(gen_random_uuid(),'{Group}','{InactiveMember}',now()-interval '1 day',NULL),
              (gen_random_uuid(),'{Group}','{Left}',now()-interval '2 days',now()-interval '1 day');
            INSERT INTO "ResourceGrant"("Id","ObjectId","UserId","GroupId","Role","StartAt","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(),'{Item}',NULL,'{HubGroup}',1,NULL,now()-interval '1 day',NULL),
              (gen_random_uuid(),'{Item}','{Future}',NULL,2,now()+interval '1 day',now()-interval '1 day',NULL),
              (gen_random_uuid(),'{Item}','{Closed}',NULL,2,NULL,now()-interval '2 days',now()-interval '1 day');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Trashy}','E','book','blue');
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ActiveFrom") VALUES(gen_random_uuid(),'{Trashy}','{Writer}',2,now()-interval '1 day');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","IsTrashed","TrashedAt","TrashedById","TrashBatchId")
              VALUES('{Trashed}','{Trashy}','{Writer}',1,true,now(),'{Writer}',gen_random_uuid());
            """);
        string[] spaces = [Spaced, Empty, Excluded, Readmitted, Trashy, Unknown];
        string[] accounts = [Wiki, Writer, Reader, Member, Inactive, Expired, HubAdmin, HubMember, HubLapsed, Future, Closed, Left, InactiveMember, Unknown];
        await using var db = new NpgsqlConnection(schema.ConnectionString);
        await db.OpenAsync();

        foreach (var space in spaces)
        {
            Assert.Equal(await Scalar(db, "SELECT atlas_space_in_ai(@space)", ("space", space)),
                await Scalar(db, $"SELECT {AccessSql.SpaceInAi("@space")}", ("space", space)));
        }
        Assert.Equal(false, await Scalar(db, $"SELECT {AccessSql.SpaceInAi("@space")}", ("space", Excluded)));
        foreach (var account in accounts)
        {
            Assert.Equal(await Scalar(db, "SELECT atlas_object_grant_role(@item, @account)", ("item", Item), ("account", account)),
                await Scalar(db, $"SELECT {AccessSql.ObjectGrantRole("@item", "@account")}", ("item", Item), ("account", account)));
        }
        Assert.Equal(2, await Scalar(db, $"SELECT {AccessSql.ObjectGrantRole("@item", "@account")}", ("item", Item), ("account", Member)));
        Assert.Equal(1, await Scalar(db, $"SELECT {AccessSql.ObjectGrantRole("@item", "@account")}", ("item", Item), ("account", HubMember)));
        foreach (var none in new[] { HubLapsed, Future, Closed, Left, InactiveMember })
        {
            Assert.Null(await Scalar(db, $"SELECT {AccessSql.ObjectGrantRole("@item", "@account")}", ("item", Item), ("account", none)));
        }
        var allowed = 0;
        foreach (var space in spaces)
        {
            foreach (var account in accounts)
            {
                foreach (var stamp in new[] { "s", "stale" })
                {
                    (string, object)[] arguments = [("space", space), ("actor", account), ("stamp", stamp)];
                    var function = await Scalar(db, "SELECT atlas_may_create_below_page(@space, @actor, @stamp)", arguments);
                    Assert.Equal(function, await Scalar(db, AccessSql.MayCreateBelowPage.Replace(":", "@"), arguments));
                    allowed += function is true ? 1 : 0;
                }
            }
        }
        Assert.Equal(11, allowed); // The local and the Hub-verified WikiAdmin in the five spaces, and the writer of a live page.
        Assert.Equal(false, await Scalar(db, AccessSql.MayCreateBelowPage.Replace(":", "@"), ("space", Trashy), ("actor", Writer), ("stamp", "s")));

        var rows = await Scalar(db, """SELECT jsonb_agg(to_jsonb(g) ORDER BY g."Id")::text FROM "ResourceGrant" g""");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110947, from: 2026110946);
        Assert.Equal(rows, await Scalar(db, """SELECT jsonb_agg(to_jsonb(g) ORDER BY g."Id")::text FROM "ResourceGrant" g"""));
        Assert.Equal(0L, await Scalar(db, """
            SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_object_grant_role','atlas_may_create_below_page')
            """));
    }

    /// <summary>An argument binds outside the predicate or it is refused: an unqualified column, an alias of the predicate's own or
    /// any other text would bind inside it (an unqualified "ObjectId" compares the grant with itself and grants every object).</summary>
    [Fact]
    public void Access_predicates_take_only_a_parameter_or_a_qualified_column()
    {
        foreach (var good in new[] { ":space", "@item", "p.\"SpaceId\"", "{alias}.\"Id\"" })
        {
            Assert.Contains(good, AccessSql.SpaceInAi(good));
            Assert.Contains(good, AccessSql.ObjectGrantRole(good, ":me"));
        }
        foreach (var bad in new[] { "\"ObjectId\"", "ObjectId", "object_grant.\"ObjectId\"", "grantee.\"Id\"", "space_ai.\"SpaceId\"",
                     "p.\"Id\" OR true", "'00000000-0000-0000-0000-000000000001'", "" })
        {
            Assert.Throws<ArgumentException>(() => AccessSql.SpaceInAi(bad));
            Assert.Throws<ArgumentException>(() => AccessSql.ObjectGrantRole(bad, ":me"));
            Assert.Throws<ArgumentException>(() => AccessSql.ObjectGrantRole(":item", bad));
        }
    }

    /// <summary>The grant role as the shared list (PersonalItemsQueryHandler) and the relation nodes embed it decides per object: a
    /// reader of one note and an editor of another sees each at its own level and a third note not at all. A rule bound to the wrong
    /// row would give every object the highest role of any.</summary>
    [PostgreSqlFact]
    public async Task The_grant_role_embedded_in_the_shared_list_and_the_relation_nodes_decides_per_object()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var (owner, grantee) = (f.Writer, f.Reader);
        async Task<Guid> NoteAsync(string title) => (await f.As<ObjectDto>(owner,
            new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["title"] = title, ["text"] = "" }))).Id;
        var (read, write, none) = (await NoteAsync("Read"), await NoteAsync("Write"), await NoteAsync("None"));
        await f.As(owner, new SetItemGrant(read, grantee.Id, null, 1, null, null, null));
        await f.As(owner, new SetItemGrant(write, grantee.Id, null, 2, null, null, null));

        var shared = await f.As<IReadOnlyList<PersonalItemDto>>(grantee, new ListSharedWithMe());
        Assert.Equal(["Read:read", "Write:write"], shared.Select(x => $"{x.Title}:{x.Level}").Order(StringComparer.Ordinal));
        using var session = f.Host.OpenSession();
        var (nodes, _) = await new RelationNodes(session, new TestRequestChannel(new TestChannelHolder()))
            .ReadAsync(grantee, null, [read, write, none], [NoteObjectType.Id], CancellationToken.None);
        Assert.Equal("read True False, write True True, none False False",
            string.Join(", ", new[] { ("read", read), ("write", write), ("none", none) }.Select(x => $"{x.Item1} {nodes[x.Item2].Readable} {nodes[x.Item2].CanEdit}")));
    }

    private static async Task<object?> Scalar(NpgsqlConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, Guid.TryParse(value as string, out var id) ? id : value);
        }
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }
}
