using System.Net;
using Npgsql;
using Atlas.Server.Platform.Identity;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class BrowserSessionRevocationTests
{
    [PostgreSqlFact]
    public async Task Logout_rejects_a_copied_cookie_but_preserves_an_independent_session()
    {
        await using var site = await AtlasHttpSite.CreateAsync("session_logout_");
        using var founder = site.Client();
        var account = await site.CompleteSetupAsync(founder);
        var login = account["username"]!.GetValue<string>();
        using var owner = site.Client();
        var setup = await AtlasHttpSite.SendAsync(owner, HttpMethod.Post, "/api/auth/login", new { username = login, password = site.Password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var cookie = Assert.Single(setup.Headers.GetValues("Set-Cookie"), x => x.StartsWith("ScaleAtlas.Session=", StringComparison.Ordinal)).Split(';')[0];
        using var replay = site.Client();
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        using var independent = site.Client();
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(independent, HttpMethod.Post,
            "/api/auth/login", new { username = login, password = site.Password })).StatusCode);
        await AtlasHttpSite.ReauthAsync(owner, site.Password);

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(owner, HttpMethod.Post, "/api/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await independent.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(owner, HttpMethod.Post, "/api/auth/logout")).StatusCode);
        Assert.Equal(1L, await site.ScalarAsync("""
            SELECT count(*) FROM "RevokedBrowserSession" WHERE "UserId" = @user
            """, ("user", account["id"]!.GetValue<Guid>())));
    }

    [PostgreSqlFact]
    public async Task Revocation_facts_survive_new_sessions_and_cannot_be_changed_or_orphaned()
    {
        await using var host = await AtlasTestHost.CreateAsync("session_revocation_schema_");
        var user = await host.SeedUserAsync("owner");
        var id = Guid.NewGuid();
        await using var sql = new NpgsqlConnection(host.ConnectionString);
        await sql.OpenAsync();
        async Task<int> Run(string text, Guid userId)
        {
            await using var command = new NpgsqlCommand(text, sql);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("user", userId);
            return await command.ExecuteNonQueryAsync();
        }

        const string insert = """
            INSERT INTO "RevokedBrowserSession" ("Id", "UserId") VALUES (@id, @user) ON CONFLICT ("Id") DO NOTHING
            """;
        var orphan = await Assert.ThrowsAsync<PostgresException>(() => Run(insert, Guid.NewGuid()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);
        Assert.Equal(1, await Run(insert, user.Id));
        Assert.Equal(0, await Run(insert, user.Id));
        using (var freshSession = host.OpenSession())
        {
            Assert.True(await BrowserSessionRevocations.IsRevokedAsync(freshSession, id, default));
            Assert.False(await BrowserSessionRevocations.IsRevokedAsync(freshSession, Guid.NewGuid(), default));
        }
        // A revocation fact is never changed or deleted: since 3.6 the application holds it (only native SQL writes the table, and
        // IntegrityRulesConventionTests refuses any UPDATE, DELETE, MERGE, upsert or TRUNCATE of it), no longer a database trigger.
    }
}
