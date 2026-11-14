using Npgsql;
using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>Migration 2026110684 over existing rows: read stays read; draft, publish and direct become write, for tokens and levels.</summary>
public sealed class ApiReadWriteScopesMigrationTests
{
    [PostgreSqlFact]
    public async Task Former_write_scopes_and_levels_become_write_and_read_stays_read()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_api_read_write_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110684);
        Guid admin = Guid.NewGuid(), reader = Guid.NewGuid(), drafter = Guid.NewGuid(), publisher = Guid.NewGuid(), director = Guid.NewGuid();
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              (@admin,'admin','Admin',true,true,'s'),(@reader,'reader','R',false,true,'s'),(@drafter,'drafter','D',false,true,'s'),
              (@publisher,'publisher','P',false,true,'s'),(@director,'director','X',false,true,'s');
            INSERT INTO "UserApiPermission"("Id","UserId","Level","CanIssueTokens","ChangedById","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(),@reader,'read',true,@admin,'2026-01-01Z',NULL),
              (gen_random_uuid(),@drafter,'publish',true,@admin,'2026-01-01Z','2026-02-01Z'),
              (gen_random_uuid(),@drafter,'draft',true,@admin,'2026-02-01Z',NULL),
              (gen_random_uuid(),@publisher,'publish',true,@admin,'2026-01-01Z',NULL),
              (gen_random_uuid(),@director,'direct',true,@admin,'2026-01-01Z',NULL),
              (gen_random_uuid(),@admin,'none',false,@admin,'2026-01-01Z',NULL);
            """, ("admin", admin), ("reader", reader), ("drafter", drafter), ("publisher", publisher), ("director", director));
        var tokens = new Dictionary<string, string[]>
        {
            ["read"] = ["read"], ["draft"] = ["read", "draft"], ["publish"] = ["read", "draft", "publish"], ["direct"] = ["read", "draft", "publish", "direct"]
        };
        var ids = new Dictionary<string, Guid>();
        foreach (var (name, scopes) in tokens)
        {
            ids[name] = Guid.NewGuid();
            await ExecuteAsync(schema.ConnectionString, """
                INSERT INTO "PersonalApiToken"("Id","UserId","Name","TokenHash","Prefix","SecurityStamp","ActiveFrom","ActiveTo","ExpiresAt") VALUES
                  (@id,@owner,@name,@hash,'sat_00000000','s',now()-interval '1 day',NULL,now()+interval '30 days');
                INSERT INTO "PersonalApiTokenScope"("TokenId","Scope") SELECT @id, unnest(@scopes);
                """, ("id", ids[name]), ("owner", director), ("name", name), ("hash", ApiSecret.Hash(ApiSecret.Create())), ("scopes", scopes));
        }

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("read", await ScopesAsync(schema.ConnectionString, ids["read"]));
        foreach (var name in new[] { "draft", "publish", "direct" })
        {
            Assert.Equal("read,write", await ScopesAsync(schema.ConnectionString, ids[name]));
        }
        Assert.Equal("admin=none|director=write|drafter=write|drafter=write|publisher=write|reader=read", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(a."Username" || '=' || p."Level", '|' ORDER BY a."Username", p."ActiveFrom")
            FROM "UserApiPermission" p JOIN "UserAccount" a ON a."Id" = p."UserId"
            """));
        // 2026111140: the widening is not silent; the security overview asks for a review of the three accounts and three tokens.
        Assert.Equal("3|3|", await ScalarAsync(schema.ConnectionString,
            """SELECT "Accounts" || '|' || "Tokens" || '|' || coalesce("ConfirmedAt"::text,'') FROM "ApiLevelsReview" """));
        // Only the two scopes and three levels remain storable; the history guard is back on.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            "INSERT INTO \"PersonalApiTokenScope\"(\"TokenId\",\"Scope\") VALUES (@id,'draft')", ("id", ids["read"])));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            "UPDATE \"UserApiPermission\" SET \"Level\"='publish' WHERE \"UserId\"=@id", ("id", reader)));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserApiPermission"("Id","UserId","Level","CanIssueTokens","ChangedById","ActiveFrom","ActiveTo")
            VALUES (gen_random_uuid(),@id,'direct',true,@id,now(),'2027-01-01Z')
            """, ("id", admin)));
    }

    /// <summary>An installation that applied 2026110684 before: only what it widened counts, and a pending review is not repeated.</summary>
    [PostgreSqlFact]
    public async Task The_review_counts_only_what_2026110684_widened_and_is_not_repeated()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_api_write_review_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026111140);
        Guid owner = Guid.NewGuid(), widened = Guid.NewGuid(), issuedSince = Guid.NewGuid();
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES (@owner,'owner','O',false,true,'s');
            INSERT INTO "UserApiPermission"("Id","UserId","Level","CanIssueTokens","ChangedById","ActiveFrom","ActiveTo")
              VALUES (gen_random_uuid(),@owner,'write',true,@owner,'2026-01-01Z',NULL);
            INSERT INTO "PersonalApiToken"("Id","UserId","Name","TokenHash","Prefix","SecurityStamp","ActiveFrom","ActiveTo","ExpiresAt") VALUES
              (@widened,@owner,'Old',@h1,'sat_00000000','s','2026-01-02Z',NULL,now()+interval '30 days'),
              (@since,@owner,'New',@h2,'sat_00000001','s',now()+interval '1 minute',NULL,now()+interval '30 days');
            INSERT INTO "PersonalApiTokenScope"("TokenId","Scope") VALUES (@widened,'write'),(@since,'write');
            """, ("owner", owner), ("widened", widened), ("since", issuedSince), ("h1", ApiSecret.Hash(ApiSecret.Create())), ("h2", ApiSecret.Hash(ApiSecret.Create())));

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("1|1", await ScalarAsync(schema.ConnectionString, """SELECT "Accounts" || '|' || "Tokens" FROM "ApiLevelsReview" """));
    }

    private static async Task<string?> ScopesAsync(string connectionString, Guid token)
        => (string?)await ScalarAsync(connectionString,
            "SELECT string_agg(\"Scope\", ',' ORDER BY \"Scope\") FROM \"PersonalApiTokenScope\" WHERE \"TokenId\"=@id", ("id", token));

    private static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
        => ScalarAsync(connectionString, sql, parameters);
}
