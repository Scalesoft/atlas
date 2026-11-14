using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.YouTrack.Contracts;
using Atlas.Server.Tests.Platform;
using Atlas.YouTrack;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>
/// The YouTrack host service of the editor's <c>youtrack</c> block on a board (interaction spec §4): the page's search and
/// resolve, bound to the board instead of a space. The board decides the space (a Reader of the board, a trashed board is 404),
/// the space's project mapping and the read cache are the page's, and YouTrack is called with no transaction open.
/// </summary>
public sealed class BoardYouTrackHttpTests
{
    private const string Settings = "/api/admin/integrations/youtrack";

    [PostgreSqlFact]
    public async Task A_board_reader_searches_and_resolves_issues_of_its_spaces_projects_through_the_page_cache()
    {
        var upstream = new Upstream();
        await using var site = await AtlasHttpSite.CreateAsync("atlas_board_yt_", settings =>
        {
            settings["Modules:Boards:Enabled"] = "true";
            settings["YouTrack:Enabled"] = "true";
            settings["YouTrack:BaseUrl"] = "";
            settings["YouTrack:Token"] = "";
        }, services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => upstream));
        var idleInTransaction = new List<long>();
        upstream.OnRequest = async () => idleInTransaction.Add((long)(await site.ScalarAsync(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state LIKE 'idle in transaction%'"))!);
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var mapped = await NewSpace(admin, "Engineering");
        var unmapped = await NewSpace(admin, "Sales");
        await Read<YouTrackSettingsDto>(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, Settings,
            new SaveYouTrackSettingsRequest(true, "https://tracker.example", "board-fixture-credential", false, [new(mapped, ["0-7"])], null)));
        var board = await NewBoard(admin, mapped);
        var elsewhere = await NewBoard(admin, unmapped);

        var reader = site.Client();
        await AddReaderAsync(site, admin, reader, mapped);
        var found = await reader.GetFromJsonAsync<YouTrackIssue[]>($"/api/boards/{board}/youtrack/search?q=work");
        Assert.Equal(("TEAM-1", "Allowed fixture", "In Progress"), (Assert.Single(found!).Key, found![0].Title, found[0].State));
        // The page's cache: the same query of the same space is not asked again.
        Assert.Single((await reader.GetFromJsonAsync<YouTrackIssue[]>($"/api/boards/{board}/youtrack/search?q=work"))!);
        Assert.Equal(2, upstream.Calls); // the project's short name, then the search
        // Another query asks YouTrack once more, but the project's short name only once per configuration.
        Assert.Single((await reader.GetFromJsonAsync<YouTrackIssue[]>($"/api/boards/{board}/youtrack/search?q=other"))!);
        Assert.Equal(3, upstream.Calls);
        var resolved = await AtlasHttpSite.Json(await reader.GetAsync($"/api/boards/{board}/youtrack/resolve?reference=TEAM-1"), HttpStatusCode.OK);
        Assert.Equal(("0-issue", "TEAM-1", "https://tracker.example/issue/TEAM-1"), (resolved["id"]!.GetValue<string>(), resolved["key"]!.GetValue<string>(), resolved["url"]!.GetValue<string>()));
        Assert.Equal(new long[] { 0, 0, 0, 0 }, idleInTransaction);
        // A paste resolves all its references in one request; these are the issue just resolved, from the page cache.
        var pasted = await Read<YouTrackIssue?[]>(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/boards/{board}/youtrack/resolve",
            new YouTrackResolveRequest(["team-1", "https://tracker.example/issue/TEAM-1"])));
        Assert.Equal(["TEAM-1", "TEAM-1"], pasted.Select(issue => issue?.Key));
        Assert.Equal(4, upstream.Calls);

        // A board in a space without projects behaves like that space on a page: no issues, and resolving reports the integration
        // missing; neither asks YouTrack.
        Assert.Empty((await admin.GetFromJsonAsync<YouTrackIssue[]>($"/api/boards/{elsewhere}/youtrack/search?q=work"))!);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.GetAsync($"/api/boards/{elsewhere}/youtrack/resolve?reference=TEAM-1")).StatusCode);
        Assert.Equal(4, upstream.Calls);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync($"/api/boards/{board}/youtrack/search?q={new string('x', 257)}")).StatusCode);

        // Who may not read the board learns nothing about it, nor does anyone about a board in the trash.
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/boards/{elsewhere}/youtrack/search?q=work")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/boards/{Guid.NewGuid()}/youtrack/resolve?reference=TEAM-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Delete, $"/api/boards/{board}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/boards/{board}/youtrack/search?q=work")).StatusCode);
        Assert.Equal(4, upstream.Calls);
    }

    [PostgreSqlFact]
    public async Task With_the_integration_off_a_board_finds_no_issues_and_never_asks_YouTrack()
    {
        var upstream = new Upstream();
        await using var site = await AtlasHttpSite.CreateAsync("atlas_board_yt_off_", settings =>
        {
            settings["Modules:Boards:Enabled"] = "true";
            settings["YouTrack:Enabled"] = "false";
        }, services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => upstream));
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var board = await NewBoard(admin, await NewSpace(admin, "Engineering"));
        Assert.Empty((await admin.GetFromJsonAsync<YouTrackIssue[]>($"/api/boards/{board}/youtrack/search?q=work"))!);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.GetAsync($"/api/boards/{board}/youtrack/resolve?reference=TEAM-1")).StatusCode);
        Assert.Equal(0, upstream.Calls);
    }

    private static async Task<Guid> NewSpace(HttpClient admin, string name)
    {
        return (await Read<JsonElement>(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces", new { name }))).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> NewBoard(HttpClient admin, Guid space)
    {
        return (await Read<JsonElement>(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{space}/boards", new { title = "Plan" }))).GetProperty("id").GetGuid();
    }

    /// <summary>A builtin account with Reader in the space, signed in on <paramref name="reader"/>.</summary>
    private static async Task AddReaderAsync(AtlasHttpSite site, HttpClient admin, HttpClient reader, Guid space)
    {
        var invitation = await Read<CreatedBuiltinUser>(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users", new { username = "reader", displayName = "Reader" }));
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/auth/activate", new { token = invitation.Token, newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/auth/login", new { username = "reader", password = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{space}/access",
            new UpdateSpaceGrant(invitation.User.Id, null, "Reader", null))).StatusCode);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>A YouTrack with one issue of the project 0-7 (short name TEAM), as YouTrackHttpTests answers.</summary>
    private sealed class Upstream : HttpMessageHandler
    {
        private int m_calls;

        public int Calls => Volatile.Read(ref m_calls);

        public Func<Task>? OnRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref m_calls);
            if (OnRequest is { } hook)
            {
                await hook();
            }
            const string issue = """{"id":"0-issue","idReadable":"TEAM-1","summary":"Allowed fixture","project":{"id":"0-7"},"customFields":[{"$type":"StateIssueCustomField","value":{"name":"In Progress"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/admin/projects/0-7", StringComparison.Ordinal) ? """{"id":"0-7","shortName":"TEAM"}"""
                    : request.RequestUri.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal) ? "[" + issue + "]" : issue)
            };
        }
    }
}
