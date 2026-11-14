using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalApi.Commands;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Features.PersonalApi.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>Upgrade from 1.5 (migration 2026100600): what a token may do does not change.</summary>
public sealed class ApiTokenRightsMigrationTests
{
    private const long RightsVersion = 2026100600;

    [PostgreSqlFact]
    public async Task Write_becomes_draft_and_publish_and_can_issue_becomes_publish()
    {
        var schema = await DisposableSchema.CreateAsync("atlas_api_rights_upgrade_");
        AtlasTestHost? host = null;
        try
        {
            await ApplyBeforeAsync(schema.ConnectionString, RightsVersion);
            var admin = Guid.NewGuid();
            var writer = Guid.NewGuid();
            var former = Guid.NewGuid();
            var writeSecret = ApiSecret.Create();
            var readSecret = ApiSecret.Create();
            var revokedWrite = Guid.NewGuid();
            var writeToken = Guid.NewGuid();
            var readToken = Guid.NewGuid();
            // 1.5 data: one account allowed to issue tokens (a read+write and a read token, a revoked read+write token), one account
            // whose permission was enabled and later disabled.
            await ExecuteAsync(schema.ConnectionString, """
                INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
                  (@admin,'admin','Admin',true,true,'admin-stamp'),(@writer,'writer','Writer',false,true,'writer-stamp'),
                  (@former,'former','Former',false,true,'former-stamp');
                INSERT INTO "UserApiPermission"("Id","UserId","CanIssueTokens","ChangedById","ActiveFrom","ActiveTo") VALUES
                  (gen_random_uuid(),@writer,true,@admin,'2026-01-01Z',NULL),
                  (gen_random_uuid(),@former,true,@admin,'2026-01-01Z','2026-02-01Z'),
                  (gen_random_uuid(),@former,false,@admin,'2026-02-01Z',NULL);
                INSERT INTO "PersonalApiToken"("Id","UserId","Name","TokenHash","Prefix","SecurityStamp","ActiveFrom","ActiveTo","ExpiresAt") VALUES
                  (@write,@writer,'Deploy',@writeHash,'sat_00000000','writer-stamp',now()-interval '1 day',NULL,now()+interval '30 days'),
                  (@read,@writer,'Report',@readHash,'sat_00000001','writer-stamp',now()-interval '1 day',NULL,now()+interval '30 days'),
                  (@revoked,@writer,'Old',@revokedHash,'sat_00000002','writer-stamp',now()-interval '2 days',now()-interval '1 day',now()+interval '30 days');
                INSERT INTO "PersonalApiTokenScope"("TokenId","Scope") VALUES
                  (@write,'read'),(@write,'write'),(@read,'read'),(@revoked,'read'),(@revoked,'write');
                """,
                ("admin", admin), ("writer", writer), ("former", former), ("write", writeToken), ("read", readToken), ("revoked", revokedWrite),
                ("writeHash", ApiSecret.Hash(writeSecret)), ("readHash", ApiSecret.Hash(readSecret)), ("revokedHash", ApiSecret.Hash(ApiSecret.Create())));

            await AtlasMigrations.MigrateAsync(schema.ConnectionString);

            Assert.Equal("write", await ScalarAsync(schema.ConnectionString,
                "SELECT \"Level\" FROM \"UserApiPermission\" WHERE \"UserId\"=@id AND \"ActiveTo\" IS NULL", ("id", writer)));
            Assert.Equal("write,none", await ScalarAsync(schema.ConnectionString,
                "SELECT string_agg(\"Level\", ',' ORDER BY \"ActiveFrom\") FROM \"UserApiPermission\" WHERE \"UserId\"=@id", ("id", former)));
            Assert.Equal("read,write", await Scopes(writeToken));
            Assert.Equal("read", await Scopes(readToken));
            Assert.Equal("read,write", await Scopes(revokedWrite));
            Assert.Equal(3L, await ScalarAsync(schema.ConnectionString, "SELECT count(*) FROM \"PersonalApiTokenChannel\" WHERE \"Channel\"='rest'"));
            Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, "SELECT count(*) FROM \"PersonalApiTokenScope\" WHERE \"Scope\" IN ('draft','publish')"));
            // The review item of the security overview names what was converted; the history guard is active again.
            Assert.Equal("1|2|", await ScalarAsync(schema.ConnectionString,
                "SELECT \"Accounts\" || '|' || \"Tokens\" || '|' || coalesce(\"ConfirmedAt\"::text,'') FROM \"ApiLevelsReview\""));
            // The history guard of UserApiPermission holds in the application since 3.6 (IntegrityRules, IntegrityRulesTests).
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
                "INSERT INTO \"PersonalApiTokenScope\"(\"TokenId\",\"Scope\") VALUES (@id,'draft')", ("id", readToken)));

            // The upgraded tokens authenticate: read+write may still read, save and publish over REST (since 2026110684 as write again).
            host = await AtlasTestHost.CreateAsync(schema, services => services.AddScoped<PersonalApiActor>());
            var credentials = new PersonalApiCredentials(host.Database);
            Assert.Equal(["read", "write"], (await credentials.AuthenticateAsync(writeSecret, ApiChannels.Rest, default))!.Scopes);
            Assert.Equal(["read"], (await credentials.AuthenticateAsync(readSecret, ApiChannels.Rest, default))!.Scopes);
            Assert.Null(await credentials.AuthenticateAsync(writeSecret, ApiChannels.Mcp, default));

            var administrator = new UserAccount { Id = admin, Username = "admin", DisplayName = "Admin", IsWikiAdmin = true, SecurityStamp = "admin-stamp" };
            host.Actor.Current = administrator;
            Assert.True((await host.InvokeAsync<ApiTokensSummary>(new GetApiTokensSummary())).LevelsReview);
            Assert.False((await host.InvokeAsync<ApiTokensSummary>(new ConfirmApiLevelsReview())).LevelsReview);
            Assert.False((await host.InvokeAsync<ApiTokensSummary>(new GetApiTokensSummary())).LevelsReview);
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
            else
            {
                await schema.DisposeAsync();
            }
        }

        Task<object?> Scopes(Guid token) => ScalarAsync(schema.ConnectionString,
            "SELECT string_agg(\"Scope\", ',' ORDER BY \"Scope\") FROM \"PersonalApiTokenScope\" WHERE \"TokenId\"=@id", ("id", token));
    }

    /// <summary>Applies the embedded scripts below a version the way 1.5 left the schema, with its journal.</summary>
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
