using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>
/// What a token's Markdown write costs before it is refused (S-H1, S-M1, S-M2): a create the token may not make is refused by a
/// short read under the space's shared lock, never under the create's space lock; limits that bound the work come before any read;
/// and a token runs at most two Markdown writes at once, its account four.
/// </summary>
public sealed class MarkdownWriteLimitTests
{
    [PostgreSqlFact]
    public async Task A_create_without_rights_is_refused_without_the_space_write_lock_and_audited_with_its_space()
    {
        await using var f = await McpSite.CreateAsync(enabled: false);
        var space = await f.SpaceAsync("Cizí");
        var (outsider, _) = await f.UserAsync("outsider", "write");
        var token = await McpSite.IssueAsync(outsider, f.Site.Password, ["read", "write"], ["rest"]);
        using var api = Bearer(f, token.Secret);
        // Another transaction reads the space FOR SHARE: the create's FOR UPDATE would wait for it, the refusal's read does not.
        await using var holder = await HoldSpaceAsync(f, space, "FOR SHARE");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var refused = await api.PostAsJsonAsync("/api/v1/pages", new { spaceId = space, title = "Návod", markdown = "# Návod\n\nText." }, deadline.Token);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal($"page.create:not_found:{space}", await f.Site.ScalarAsync(
            """SELECT "Operation" || ':' || "Outcome" || ':' || "SpaceId" FROM "ApiActivity" WHERE "TokenId"=@token""", ("token", Guid.Parse(token.Id))));
    }

    [PostgreSqlFact]
    public async Task Too_many_component_removals_are_refused_before_the_page_is_read()
    {
        await using var f = await McpSite.CreateAsync(enabled: false);
        await f.SetLevelAsync(f.AdminId, "write");
        using var api = Bearer(f, (await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"])).Secret);

        // An unknown page: the cap answers before the page gate could say 404.
        var refused = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{Guid.NewGuid()}", new
        {
            markdown = "Text.", expectedVersion = 1, removeComponents = Enumerable.Range(1, PageMarkdownSaveRunner.MaxRemoveComponents + 1)
        }), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_argument", refused["code"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Concurrent_markdown_writes_are_bounded_per_token_and_per_account_with_429_and_retry_after()
    {
        // Isolated: the blocked creates hold the installation's content lock shared; another test's administration (content lock
        // exclusive) queues behind them on a shared site, and the creates sent after it queue behind that instead of the space.
        await using var f = await McpSite.CreateAsync(enabled: false, isolated: true);
        var space = await f.FirstSpaceAsync();
        await f.SetLevelAsync(f.AdminId, "write");
        var tokens = new List<HttpClient>();
        for (var index = 0; index < 3; index++)
        {
            tokens.Add(Bearer(f, (await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"], "Token " + index)).Secret));
        }
        // Creates wait for the space while another transaction holds it; each keeps its permits until it answers.
        await using var holder = await HoldSpaceAsync(f, space, "FOR UPDATE");
        Task<HttpResponseMessage> Create(HttpClient api, string title) => api.PostAsJsonAsync("/api/v1/pages", new { spaceId = space, title });
        var running = new List<Task<HttpResponseMessage>> { Create(tokens[0], "A1"), Create(tokens[0], "A2") };
        await WaitForBlockedAsync(f, holder, 2);

        // The token's third: over its 2.
        using var third = await Create(tokens[0], "A3");
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("1", third.Headers.GetValues("Retry-After").Single());

        // Another token of the same account takes the account to its 4; a third token's first write is over the account's limit.
        running.AddRange([Create(tokens[1], "B1"), Create(tokens[1], "B2")]);
        await WaitForBlockedAsync(f, holder, 4);
        using var account = await Create(tokens[2], "C1");
        Assert.Equal(HttpStatusCode.TooManyRequests, account.StatusCode);
        Assert.Equal("1", account.Headers.GetValues("Retry-After").Single());

        await holder.CloseAsync();
        Assert.All(await Task.WhenAll(running), response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        tokens.ForEach(client => client.Dispose());
    }

    /// <summary>A connection whose open transaction holds the space row with the given lock until it is disposed.</summary>
    private static async Task<NpgsqlConnection> HoldSpaceAsync(McpSite f, string spaceId, string strength)
    {
        var connection = new NpgsqlConnection(f.Site.ConnectionString);
        await connection.OpenAsync();
        await using (var begin = new NpgsqlCommand("BEGIN", connection))
        {
            await begin.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand($"""SELECT 1 FROM "Space" WHERE "Id"=@space {strength}""", connection);
        command.Parameters.AddWithValue("space", Guid.Parse(spaceId));
        Assert.Equal(1, await command.ExecuteScalarAsync());
        return connection;
    }

    private static async Task WaitForBlockedAsync(McpSite f, NpgsqlConnection holder, long count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((long)(await f.Site.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid))",
                   ("holder", holder.ProcessID)))! < count)
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private static HttpClient Bearer(McpSite f, string secret)
    {
        var client = f.Site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }
}
