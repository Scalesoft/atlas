using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.ApiActivity.Commands;
using Atlas.Server.Tests.PersonalApi;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.ApiActivity;

/// <summary>T3: every write attempt over the personal API (and later MCP) leaves an audit fact — action, target ids, outcome code,
/// channel and token — written in its own short transaction, so a refusal whose handler rolled back is audited too. Never content.</summary>
public sealed class ApiActivityTests
{
    private const string Secret = "Tajný obsah 7f3a";
    private const string WorkingTitle = "Pracovní název 51c2";
    /// <summary>The personal-api policy: 120 requests a minute per token (AtlasServices).</summary>
    private const int RequestsPerMinute = 120;
    private static readonly string Rich = $$$"""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{{Secret}}}"}]}]}}""";

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Api_writes_are_audited_with_outcome_and_without_content()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_api_activity_");
        using var web = site.Client();
        var ownerId = (await site.CompleteSetupAsync(web))["id"]!.GetValue<string>();
        var (tokenId, api) = await IssueTokenAsync(site, web, ownerId, "write", ["read", "write"]);
        Guid pageId;
        string spaceId;
        using (api)
        {
            spaceId = await AtlasHttpSite.OwnSpaceAsync(web);
            var page = await AtlasHttpSite.Json(await api.PostAsJsonAsync("/api/v1/pages",
                new { spaceId, parentId = (string?)null, title = WorkingTitle, markdown = Secret }), HttpStatusCode.Created);
            pageId = Guid.Parse(page["id"]!.GetValue<string>());
            await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{pageId}",
                new { title = WorkingTitle, markdown = Secret + " 2", expectedVersion = 1 }), HttpStatusCode.OK);
            var stale = await api.PutAsJsonAsync($"/api/v1/pages/{pageId}",
                new { title = WorkingTitle, markdown = Secret + " 3", expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal("version_conflict", (await stale.Content.ReadFromJsonAsync<JsonNode>())!["code"]!.GetValue<string>());
        }

        // The stale save rolled its transaction back (the page stays at version 2); its audit row was written afterwards and stayed.
        Assert.Equal(2, await site.ScalarAsync("SELECT \"Version\" FROM \"Page\" WHERE \"Id\"=@p", ("p", pageId)));
        Assert.Equal("page.create:ok:1,page.save:ok:2,page.save:version_conflict:-", await site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome" || ':' || coalesce("ResultVersion"::text,'-'), ',' ORDER BY "Id") FROM "ApiActivity" WHERE "ActorId"=@a::uuid
            """, ("a", ownerId)));
        Assert.Equal(3L, await site.ScalarAsync("""
            SELECT count(*) FROM "ApiActivity" WHERE "Channel"='api' AND "TokenId"=@t AND "ActorId"=@a::uuid AND "PageId"=@p
                AND "SpaceId"=@s::uuid AND "RequestId" IS NOT NULL
            """, ("t", tokenId), ("a", ownerId), ("p", pageId), ("s", spaceId)));
        Assert.Equal("api.write,api.write,api.write", await site.ScalarAsync("""
            SELECT string_agg(e."Action", ',' ORDER BY e."Id") FROM "AccessAuditEvent" e WHERE e."ApiActivityId" IS NOT NULL AND e."ActorId"=@a::uuid
            """, ("a", ownerId)));

        // Neither the fact nor its view carries the page text, its working title or the refusal message.
        var stored = (string)(await site.ScalarAsync("""
            SELECT (SELECT string_agg(row_to_json(a)::text, E'\n') FROM "ApiActivity" a WHERE a."ActorId"=@a::uuid) || E'\n' ||
                   (SELECT string_agg(row_to_json(v)::text, E'\n') FROM "AccessAuditView" v WHERE v."ApiOperation" IS NOT NULL AND v."ActorId"=@a::uuid)
            """, ("a", ownerId)))!;
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkingTitle, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("Stránku mezitím změnil", stored, StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task Refused_scope_and_hidden_page_are_audited_without_leaking()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_api_activity_refused_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        // A page the token owner may not read: the default space grants the new account nothing.
        var hidden = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = WorkingTitle, contentJson = Rich, markdown = Secret }), HttpStatusCode.OK);
        var hiddenId = Guid.Parse(hidden["id"]!.GetValue<string>());

        var (writerId, writerWeb) = await InviteAsync(site, admin, "writer");
        var (tokenId, api) = await IssueTokenAsync(site, writerWeb, writerId, "write", ["read", "write"], admin);
        using (writerWeb)
        using (api)
        {
            // The write scope reaches the handlers, and the owner's rights refuse both as a missing page; each refusal is audited.
            var refused = await api.PostAsJsonAsync($"/api/v1/pages/{hiddenId}/publish", new { expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.DoesNotContain(WorkingTitle, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var missing = await api.PutAsJsonAsync($"/api/v1/pages/{hiddenId}",
                new { title = "Probe", markdown = "Probe", expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.DoesNotContain(WorkingTitle, await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal("page.publish:not_found,page.save:not_found", await site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome", ',' ORDER BY "Id") FROM "ApiActivity"
            WHERE "ActorId"=@a::uuid AND "TokenId"=@t AND "PageId"=@p AND "SpaceId"=@s::uuid AND "ResultVersion" IS NULL
            """, ("a", writerId), ("t", tokenId), ("p", hiddenId), ("s", spaceId)));
        Assert.Equal("api.write,api.write", await site.ScalarAsync("""
            SELECT string_agg("Action", ',' ORDER BY "Id") FROM "AccessAuditView" WHERE "ActorId"=@a::uuid AND "ApiOperation" IS NOT NULL
            """, ("a", writerId)));

        // The view names a page only by its published title: nothing while it is a draft, never a later working title.
        Assert.Equal(DBNull.Value, await PageTitlesAsync(site, writerId));
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{hiddenId}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{hiddenId}/title",
            new { title = "Nový pracovní název", expectedVersion = 1 })).StatusCode);
        Assert.Equal($"{WorkingTitle}|{WorkingTitle}", await PageTitlesAsync(site, writerId));
    }

    [PostgreSqlFact]
    public async Task Reads_and_rate_limited_requests_are_not_audited()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_api_activity_limit_");
        using var web = site.Client();
        var ownerId = (await site.CompleteSetupAsync(web))["id"]!.GetValue<string>();
        var (_, api) = await IssueTokenAsync(site, web, ownerId, "write", ["read", "write"]);
        using (api)
        {
            var spaceId = await AtlasHttpSite.OwnSpaceAsync(web);
            Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/api/v1/spaces/{spaceId}/pages")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/search?q=atlas")).StatusCode);
            // Exhaust the token's window (120 a minute) with reads, then a write is refused by the limiter before its filter runs.
            var limited = false;
            for (var attempt = 0; attempt < 130 && !limited; attempt++)
            {
                limited = (await api.GetAsync("/api/v1/spaces")).StatusCode == HttpStatusCode.TooManyRequests;
            }
            Assert.True(limited);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await api.PutAsJsonAsync($"/api/v1/pages/{Guid.NewGuid()}",
                new { title = "Limited", markdown = "Limited", expectedVersion = 1 })).StatusCode);
        }
        Assert.Equal(0L, await site.ScalarAsync("SELECT count(*) FROM \"ApiActivity\" WHERE \"ActorId\"=@a::uuid", ("a", ownerId)));
        Assert.Equal(0L, await site.ScalarAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"ActorId\"=@a::uuid AND (\"Action\" LIKE 'api.%' OR \"Action\" LIKE 'mcp.%')", ("a", ownerId)));
    }

    /// <summary>A refused scope spends the token's request budget like any other request: the limiter runs before authorization, so a
    /// flood of refused writes leaves at most one window of scope_missing rows, then 429s that write nothing. The budget is the token's,
    /// not the client address's: another token from the same address keeps writing, and its writes stay audited.</summary>
    [PostgreSqlFact]
    public async Task Refused_scope_floods_are_rate_limited_per_token_and_their_audit_stays_bounded()
    {
        await using var site = await RightsSite.CreateAsync();
        await site.SetLevelAsync("write");
        var reader = await site.TokenClientAsync(["read"]);
        var writer = await site.TokenClientAsync(["read", "write"]);
        var spaceId = await site.FirstSpaceAsync(writer);

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < RequestsPerMinute + 10; attempt++)
        {
            statuses.Add((await reader.PutAsJsonAsync($"/api/v1/pages/{Guid.NewGuid()}",
                new { title = "Flood", markdown = "Flood", expectedVersion = 1 })).StatusCode);
        }
        Assert.All(statuses.Take(RequestsPerMinute), status => Assert.Equal(HttpStatusCode.Forbidden, status));
        Assert.All(statuses.Skip(RequestsPerMinute), status => Assert.Equal(HttpStatusCode.TooManyRequests, status));
        Assert.Equal((long)RequestsPerMinute, await site.Site.ScalarAsync("SELECT count(*) FROM \"ApiActivity\" WHERE \"ActorId\"=@a::uuid AND \"Outcome\"='scope_missing'", ("a", site.OwnerId)));
        Assert.Equal((long)RequestsPerMinute, await site.Site.ScalarAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"ActorId\"=@a::uuid AND \"Action\"='api.write'", ("a", site.OwnerId)));

        // The writer's budget is its own (one GET above): its write goes through and is audited.
        var created = await AtlasHttpSite.Json(await writer.PostAsJsonAsync("/api/v1/pages",
            new { spaceId, parentId = (string?)null, title = "Legitimate", markdown = "Legitimate" }), HttpStatusCode.Created);
        Assert.Equal(1L, await site.Site.ScalarAsync("SELECT count(*) FROM \"ApiActivity\" WHERE \"Outcome\"='ok' AND \"PageId\"=@p",
            ("p", Guid.Parse(created["id"]!.GetValue<string>()))));
    }

    [PostgreSqlFact]
    public async Task Audit_rows_are_append_only()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_api_activity_rows_");
        var owner = await host.SeedUserAsync("owner");
        var tokenId = await TestApiTokens.SeedAsync(host, owner, "Claude Code");
        var pageId = Guid.NewGuid();
        // The MCP tool filter (C1–C3) records through the same command; atlas_publish is the one publishing tool.
        await host.InvokeAsync(new RecordApiActivity(owner.Id, tokenId, "mcp", "atlas_update_section", pageId, null, ApiActivityOutcomes.Ok, 3, "trace-1"));
        await host.InvokeAsync(new RecordApiActivity(owner.Id, tokenId, "mcp", "atlas_publish", pageId, null, "publish_confirmation_required", null, "trace-2"));
        using (var session = host.OpenSession())
        {
            var actions = await session.CreateSQLQuery("""
                SELECT e."Action" || ':' || a."Operation" FROM "AccessAuditEvent" e JOIN "ApiActivity" a ON a."Id"=e."ApiActivityId" WHERE a."TokenId"=:token ORDER BY e."Id"
                """).SetParameter("token", tokenId).ListAsync<string>();
            Assert.Equal(["mcp.write:atlas_update_section", "mcp.publish:atlas_publish"], actions);
        }

        // Append-only: no server code updates or deletes these rows (IntegrityRulesConventionTests, C# since 3.6).
        // The audit actor is always the token's owner.
        var stranger = await host.SeedUserAsync("stranger");
        await Assert.ThrowsAnyAsync<Exception>(() => host.InvokeAsync(
            new RecordApiActivity(stranger.Id, tokenId, "api", "page.save", pageId, null, ApiActivityOutcomes.Ok, 1, null)));
    }

    /// <summary>The MCP module switch (logic in C1): temporal like the other module settings, history guarded, every new current row audited.</summary>
    [PostgreSqlFact]
    public async Task Mcp_setting_changes_are_audited_and_history_is_immutable()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_mcp_setting_") /* own database: McpSetting is one temporal row (the module switch) for the whole installation; the test asserts its history and its single current row */;
        var admin = await host.SeedUserAsync("admin", true);
        // The module's save writes the setting and its audit with the actor (C# since 3.6).
        host.Actor.Current = admin;
        var enabled = await host.InvokeAsync<Atlas.Server.Features.Mcp.Contracts.McpSettingsView>(
            new Atlas.Server.Features.Mcp.Commands.SaveMcpSettings(new Atlas.Server.Features.Mcp.Contracts.SaveMcpRequest(true, null)));
        await host.InvokeAsync<Atlas.Server.Features.Mcp.Contracts.McpSettingsView>(
            new Atlas.Server.Features.Mcp.Commands.SaveMcpSettings(new Atlas.Server.Features.Mcp.Contracts.SaveMcpRequest(false, enabled.Version)));
        using (var session = host.OpenSession())
        {
            var actions = await session.CreateSQLQuery("""
                SELECT "Action" || ':' || CAST("ActorId" AS text) FROM "AccessAuditEvent" WHERE "McpSettingId" IS NOT NULL ORDER BY "Id"
                """).ListAsync<string>();
            Assert.Equal([$"mcp.enabled:{admin.Id}", $"mcp.disabled:{admin.Id}"], actions);
        }
        await IntegrityRefusal.AssertAsync(host, IntegrityRules.HistoryImmutable, async s =>
            (await s.QueryOver<Atlas.Server.Features.Mcp.Domain.McpSetting>().Where(x => x.ActiveTo != null).Take(1).SingleOrDefaultAsync())!.Enabled = false);
        // A second current row is impossible.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(host,
            $"INSERT INTO \"McpSetting\"(\"Id\",\"Enabled\",\"ChangedById\",\"ActiveFrom\") VALUES (gen_random_uuid(),true,'{admin.Id}',now() + interval '1 hour')"));
    }

    private static async Task ExecuteAsync(AtlasTestHost host, string sql)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<object?> PageTitlesAsync(AtlasHttpSite site, string actorId)
        => site.ScalarAsync("SELECT string_agg(\"PageTitle\", '|' ORDER BY \"Id\") FROM \"AccessAuditView\" WHERE \"ActorId\"=@a::uuid AND \"ApiOperation\" IS NOT NULL",
            ("a", actorId));

    /// <summary>A signed-in account without any space grant.</summary>
    internal static async Task<(string Id, HttpClient Web)> InviteAsync(AtlasHttpSite site, HttpClient admin, string username)
    {
        username = TestLogin.For(username);
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username, displayName = username }), HttpStatusCode.Created);
        var web = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(web, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(web, HttpMethod.Post, "/api/auth/login", new { username, password = site.Password }), HttpStatusCode.OK);
        return (invitation["user"]!["id"]!.GetValue<string>(), web);
    }

    /// <summary>Sets the owner's level (through the administrator, or the owner when it is the administrator) and issues a token.</summary>
    internal static async Task<(Guid TokenId, HttpClient Client)> IssueTokenAsync(AtlasHttpSite site, HttpClient owner, string ownerId, string level,
        string[] scopes, HttpClient? administrator = null, string name = "Claude Code – notebook")
    {
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(administrator ?? owner, HttpMethod.Put, $"/api/admin/users/{ownerId}/api-access",
            new { level, expectedVersion = (string?)null }), HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(owner, site.Password);
        var token = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(owner, HttpMethod.Post, "/api/profile/api-tokens",
            new { name, scopes, expiresInDays = 1 }), HttpStatusCode.Created);
        var client = site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["secret"]!.GetValue<string>());
        return (Guid.Parse(token["token"]!["id"]!.GetValue<string>()), client);
    }
}
