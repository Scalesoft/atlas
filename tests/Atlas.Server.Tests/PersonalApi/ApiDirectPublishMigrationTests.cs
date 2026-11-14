using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>Migration 2026102020: the level and the scope direct become possible; existing levels and token scopes stay as they were.</summary>
public sealed class ApiDirectPublishMigrationTests
{
    private const long DirectVersion = 2026102020;

    [PostgreSqlFact]
    public async Task Existing_levels_and_scopes_stay_and_direct_becomes_valid()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_api_direct_");
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        // The schema as it was before this migration, with rows of an installation that used publish tokens.
        await ExecuteAsync(schema.ConnectionString, $"""
            ALTER TABLE "UserApiPermission" DROP CONSTRAINT "CK_UserApiPermission_Level",
                ADD CONSTRAINT "CK_UserApiPermission_Level" CHECK ("Level" IN ('none','read','draft','publish'));
            ALTER TABLE "PersonalApiTokenScope" DROP CONSTRAINT "CK_PersonalApiTokenScope_Scope",
                ADD CONSTRAINT "CK_PersonalApiTokenScope_Scope" CHECK ("Scope" IN ('read','draft','publish'));
            DELETE FROM "SchemaVersion" WHERE "Version" = {DirectVersion};
            """);
        var admin = Guid.NewGuid();
        var writer = Guid.NewGuid();
        var token = Guid.NewGuid();
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              (@admin,'admin','Admin',true,true,'admin-stamp'),(@writer,'writer','Writer',false,true,'writer-stamp');
            INSERT INTO "UserApiPermission"("Id","UserId","Level","CanIssueTokens","ChangedById","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(),@writer,'publish',true,@admin,'2026-01-01Z',NULL);
            INSERT INTO "PersonalApiToken"("Id","UserId","Name","TokenHash","Prefix","SecurityStamp","ActiveFrom","ActiveTo","ExpiresAt") VALUES
              (@token,@writer,'Agent',@hash,'sat_00000000','writer-stamp',now()-interval '1 day',NULL,now()+interval '30 days');
            INSERT INTO "PersonalApiTokenScope"("TokenId","Scope") VALUES (@token,'read'),(@token,'draft'),(@token,'publish');
            """, ("admin", admin), ("writer", writer), ("token", token), ("hash", Features.PersonalApi.Domain.ApiSecret.Hash(Features.PersonalApi.Domain.ApiSecret.Create())));

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("publish", await ScalarAsync(schema.ConnectionString,
            "SELECT \"Level\" FROM \"UserApiPermission\" WHERE \"UserId\"=@id AND \"ActiveTo\" IS NULL", ("id", writer)));
        Assert.Equal("draft,publish,read", await ScalarAsync(schema.ConnectionString,
            "SELECT string_agg(\"Scope\", ',' ORDER BY \"Scope\") FROM \"PersonalApiTokenScope\" WHERE \"TokenId\"=@id", ("id", token)));
        await ExecuteAsync(schema.ConnectionString, "INSERT INTO \"PersonalApiTokenScope\"(\"TokenId\",\"Scope\") VALUES (@id,'direct')", ("id", token));
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserApiPermission"("Id","UserId","Level","CanIssueTokens","ChangedById","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(),@admin,'direct',true,@admin,now(),NULL);
            """, ("admin", admin));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            "INSERT INTO \"PersonalApiTokenScope\"(\"TokenId\",\"Scope\") VALUES (@id,'write')", ("id", token)));
    }

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
