using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Platform;
using Atlas.YouTrack;
using Xunit;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.YouTrack.Contracts;
using Atlas.Server.Features.YouTrack.Domain;
namespace Atlas.Server.Tests;

/// <summary>Every test has a site of its own: it saves the installation's one current YouTrackConfiguration row (and steers a fake
/// YouTrack the site is built with).</summary>
public sealed class YouTrackHttpTests
{
    private const string Endpoint = "/api/admin/integrations/youtrack";
    private const string SyntheticToken = "youtrack-http-fixture-credential";

    [PostgreSqlFact]
    public async Task Settings_are_temporal_encrypted_admin_only_and_test_the_saved_connection()
    {
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        site.OnDisposing = () => transport.Hold?.TrySetResult();
        using var admin = site.Client();
        await Setup(site, admin);
        var initial = await admin.GetFromJsonAsync<YouTrackSettingsDto>(Endpoint);
        Assert.True(initial!.ModuleEnabled); Assert.False(initial.Enabled); Assert.False(initial.HasToken);
        var space = await NewSpace(admin);
        var body = new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(space, ["0-7"])], null);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Endpoint, body)).StatusCode); // CSRF
        var savedResponse = await Send(admin, HttpMethod.Put, Endpoint, body);
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var text = await savedResponse.Content.ReadAsStringAsync(); Assert.DoesNotContain(SyntheticToken, text);
        var saved = JsonSerializer.Deserialize<YouTrackSettingsDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(saved.HasToken); Assert.NotNull(saved.Version); Assert.Equal("configured", saved.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(admin, HttpMethod.Put, Endpoint, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(admin, HttpMethod.Post, Endpoint + "/test")).StatusCode);
        Assert.Equal(1, transport.Calls);
        var updated = await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, body with { Token = null, ExpectedVersion = saved.Version, SpaceProjects = [new(space, ["0-8"])] }));
        Assert.True(updated.HasToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(admin, HttpMethod.Put, Endpoint, body with { Token = null, BaseUrl = "https://another.example", ExpectedVersion = updated.Version })).StatusCode);
        using var scope = site.Services.CreateScope(); using var session = scope.ServiceProvider.GetRequiredService<AtlasDatabase>().OpenSession();
        var rows = await session.QueryOver<YouTrackConfiguration>().OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(2, rows.Count); Assert.Equal(rows[0].ActiveTo, rows[1].ActiveFrom); Assert.Null(rows[1].ActiveTo);
        Assert.All(rows, row => { Assert.NotEmpty(row.ProtectedToken); Assert.DoesNotContain(SyntheticToken, row.ProtectedToken); });
        Assert.DoesNotContain(SyntheticToken, body.ToString());
    }

    [PostgreSqlFact]
    public async Task Cache_never_bypasses_current_reader_access_or_changed_project_scope()
    {
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        site.OnDisposing = () => transport.Hold?.TrySetResult();
        using var admin = site.Client(); await Setup(site, admin);
        var space = await NewSpace(admin);
        var saved = await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(space, ["0-7"])], null)));
        var created = await Read<CreatedBuiltinUser>(await Send(admin, HttpMethod.Post, "/api/admin/users", new { username = "reader", displayName = "Reader" }));
        using var reader = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(reader, HttpMethod.Post, "/api/auth/activate", new { token = created.Token, newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(reader, HttpMethod.Post, "/api/auth/login", new { username = "reader", password = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Endpoint)).StatusCode);
        // A space the reader does not read is not named among the spaces with the integration.
        Assert.Empty((await reader.GetFromJsonAsync<Guid[]>("/api/integrations/youtrack/spaces"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(reader, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(false, "", null, false, [], saved.Version))).StatusCode);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new UpdateSpaceGrant(created.User.Id, null, "Reader", null));
        var url = $"/api/integrations/youtrack/search?spaceId={space}&q=work";
        Assert.Equal([space], (await reader.GetFromJsonAsync<Guid[]>("/api/integrations/youtrack/spaces"))!);
        var first = await reader.GetFromJsonAsync<YouTrackIssue[]>(url); Assert.Equal("0-issue", Assert.Single(first!).Id);
        Assert.Single((await reader.GetFromJsonAsync<YouTrackIssue[]>(url))!); Assert.Equal(2, transport.Calls);
        var access = await admin.GetFromJsonAsync<SpaceAccessSummary>($"/api/spaces/{space}/access");
        var grant = Assert.Single(access!.Grants, x => x.UserId == created.User.Id);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new UpdateSpaceGrant(created.User.Id, null, null, grant.Version));
        var denied = await reader.GetAsync(url); Assert.True(denied.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden); Assert.Equal(2, transport.Calls);
        await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", null, false, [new(space, ["0-8"])], saved.Version));
        // Project 0-8 has no readable short name here, so there is nothing to search.
        Assert.Empty((await admin.GetFromJsonAsync<YouTrackIssue[]>(url))!); Assert.Equal(3, transport.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/integrations/youtrack/resolve?spaceId={space}&reference=TEAM-1")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Search_in_a_space_without_the_integration_is_empty_without_an_upstream_call()
    {
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        using var admin = site.Client(); await Setup(site, admin);
        var linked = await NewSpace(admin);
        var unlinked = await NewSpace(admin);
        // Omnisearch asks every searched space; an unconfigured integration is not an outage (no 503, no retry).
        Assert.Empty((await admin.GetFromJsonAsync<YouTrackIssue[]>($"/api/integrations/youtrack/search?spaceId={linked}&q=work"))!);
        // UX SE3-2: the omnisearch asks only the spaces this lists, so an unconfigured integration costs no request per space.
        Assert.Empty((await admin.GetFromJsonAsync<Guid[]>("/api/integrations/youtrack/spaces"))!);
        Assert.Null((await admin.GetFromJsonAsync<YouTrackReaderSetup>("/api/integrations/youtrack"))!.BaseUrl);
        await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(linked, ["0-7"])], null)));
        Assert.Empty((await admin.GetFromJsonAsync<YouTrackIssue[]>($"/api/integrations/youtrack/search?spaceId={unlinked}&q=work"))!);
        Assert.Equal(0, transport.Calls);
        Assert.Equal([linked], (await admin.GetFromJsonAsync<Guid[]>("/api/integrations/youtrack/spaces"))!);
        // The editor recognises a pasted issue URL by the address its issue URLs start with.
        var setup = (await admin.GetFromJsonAsync<YouTrackReaderSetup>("/api/integrations/youtrack"))!;
        Assert.Equal([linked], setup.SpaceIds);
        Assert.Equal("https://tracker.example", setup.BaseUrl);
        Assert.Single((await admin.GetFromJsonAsync<YouTrackIssue[]>($"/api/integrations/youtrack/search?spaceId={linked}&q=work"))!);
        Assert.Equal(2, transport.Calls);
        // Resolving an explicit reference still reports the missing integration.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.GetAsync($"/api/integrations/youtrack/resolve?spaceId={unlinked}&reference=TEAM-1")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Permission_configuration_change_during_upstream_request_discards_old_result()
    {
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        site.OnDisposing = () => transport.Hold?.TrySetResult();
        using var admin = site.Client(); await Setup(site, admin);
        var space = await NewSpace(admin);
        var saved = await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(space, ["0-7"])], null)));
        transport.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = admin.GetAsync($"/api/integrations/youtrack/search?spaceId={space}&q=work");
        // Full database suites can queue the in-process request behind other host work; this only
        // bounds fixture readiness and is not a product HTTP timeout.
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(false, "https://tracker.example", null, false, [], saved.Version))).StatusCode);
        transport.Hold.SetResult();
        Assert.Equal(HttpStatusCode.Conflict, (await pending).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Upstream_is_called_with_no_database_transaction_open()
    {
        // Why: a YouTrack call may take up to 8 s. A transaction open across it keeps one pooled connection "idle in
        // transaction" for that long, and a slow YouTrack then starves the pool of every other request (docs/architecture.md
        // section 5: the YouTrack API does not belong in an open transaction). A second connection counts such sessions while the
        // fake YouTrack is answering, for each of the three upstream calls: search, resolve and "Test spojení". The same run checks that
        // each of the three calls carries the stored token, which the runner now unprotects instead of the handler.
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        var idleInTransaction = new List<long>();
        transport.OnRequest = async () => idleInTransaction.Add((long)(await site.ScalarAsync(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state LIKE 'idle in transaction%'"))!);
        using var admin = site.Client(); await Setup(site, admin);
        var space = await NewSpace(admin);
        await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(space, ["0-7"])], null)));
        Assert.Single((await admin.GetFromJsonAsync<YouTrackIssue[]>($"/api/integrations/youtrack/search?spaceId={space}&q=work"))!);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/integrations/youtrack/resolve?spaceId={space}&reference=TEAM-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(admin, HttpMethod.Post, Endpoint + "/test")).StatusCode);
        Assert.Equal(4, transport.Calls); // the search looks up the project's short name first
        Assert.Equal(new long[] { 0, 0, 0, 0 }, idleInTransaction);
        Assert.All(transport.Authorizations, authorization => Assert.Equal("Bearer " + SyntheticToken, authorization));
    }

    [PostgreSqlFact]
    public async Task Access_revoked_during_upstream_request_discards_the_result_and_is_not_cached()
    {
        // Why: the authorization after the upstream call is what stops a reader whose access was revoked meanwhile from receiving
        // what YouTrack answered afterwards, and from leaving it in the cache shared by everyone else.
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        site.OnDisposing = () => transport.Hold?.TrySetResult();
        using var admin = site.Client(); await Setup(site, admin);
        var space = await NewSpace(admin);
        await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(space, ["0-7"])], null)));
        var created = await Read<CreatedBuiltinUser>(await Send(admin, HttpMethod.Post, "/api/admin/users", new { username = "reader", displayName = "Reader" }));
        using var reader = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(reader, HttpMethod.Post, "/api/auth/activate", new { token = created.Token, newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(reader, HttpMethod.Post, "/api/auth/login", new { username = "reader", password = site.Password })).StatusCode);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new UpdateSpaceGrant(created.User.Id, null, "Reader", null));
        var url = $"/api/integrations/youtrack/search?spaceId={space}&q=work";
        transport.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = reader.GetAsync(url);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var access = await admin.GetFromJsonAsync<SpaceAccessSummary>($"/api/spaces/{space}/access");
        var grant = Assert.Single(access!.Grants, x => x.UserId == created.User.Id);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new UpdateSpaceGrant(created.User.Id, null, null, grant.Version));
        transport.Hold.SetResult();
        Assert.Equal(HttpStatusCode.NotFound, (await pending).StatusCode);
        // Access granted again: the same query reaches YouTrack once more, so the discarded answer was not cached.
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new UpdateSpaceGrant(created.User.Id, null, "Reader", null));
        Assert.Single((await reader.GetFromJsonAsync<YouTrackIssue[]>(url))!);
        Assert.Equal(4, transport.Calls);
    }

    [PostgreSqlFact]
    public async Task Unreadable_stored_token_is_503_before_any_upstream_call_after_the_authorization()
    {
        // Why: the token is unprotected by the runner after the first transaction, no longer by the handler inside it. Callers must
        // still see what they always saw when the key ring cannot read the stored token: the authorization first (a reader without a
        // role gets 404), then 503 on every route, also where nothing would have called YouTrack (a cached answer, a space without
        // the integration), and never an upstream call.
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        using var admin = site.Client(); await Setup(site, admin);
        var linked = await NewSpace(admin);
        var unlinked = await NewSpace(admin);
        await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(linked, ["0-7"])], null)));
        var created = await Read<CreatedBuiltinUser>(await Send(admin, HttpMethod.Post, "/api/admin/users", new { username = "outsider", displayName = "Outsider" }));
        using var outsider = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(outsider, HttpMethod.Post, "/api/auth/activate", new { token = created.Token, newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(outsider, HttpMethod.Post, "/api/auth/login", new { username = "outsider", password = site.Password })).StatusCode);
        var search = $"/api/integrations/youtrack/search?spaceId={linked}&q=work";
        Assert.Single((await admin.GetFromJsonAsync<YouTrackIssue[]>(search))!); // fills the cache
        Assert.Equal(1, await site.ExecuteAsync("UPDATE \"YouTrackConfiguration\" SET \"ProtectedToken\"='not-a-protected-payload' WHERE \"ActiveTo\" IS NULL"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.GetAsync(search)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.GetAsync($"/api/integrations/youtrack/search?spaceId={unlinked}&q=work")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.GetAsync($"/api/integrations/youtrack/resolve?spaceId={linked}&reference=TEAM-1")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(admin, HttpMethod.Post, Endpoint + "/test")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(search)).StatusCode);
        Assert.Equal(2, transport.Calls);
    }

    [PostgreSqlFact]
    public async Task Many_references_are_one_upstream_search_and_misses_are_cached_under_the_issue_key()
    {
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        using var admin = site.Client(); await Setup(site, admin);
        var space = await NewSpace(admin);
        await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(space, ["0-7"])], null)));
        var resolve = $"/api/integrations/youtrack/resolve?spaceId={space}";
        var issues = await Read<YouTrackIssue?[]>(await Send(admin, HttpMethod.Post, resolve,
            new YouTrackResolveRequest(["TEAM-1", "team-2", "https://tracker.example/issue/TEAM-1#focus=Comments-4-1.0-0"])));
        Assert.Equal(["TEAM-1", null, "TEAM-1"], issues.Select(issue => issue?.Key));
        Assert.Equal(2, transport.Calls); // the project's short name, then one search for both issues
        // Another spelling of the same issue and the issue that is not there are both answered from the cache.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{resolve}&reference=team-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{resolve}&reference=TEAM-2")).StatusCode);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(admin, HttpMethod.Post, resolve, new YouTrackResolveRequest(Enumerable.Range(1, 101).Select(i => $"TEAM-{i}").ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(admin, HttpMethod.Post, resolve, new YouTrackResolveRequest(["https://evil.example/issue/TEAM-1"]))).StatusCode);
        Assert.Equal(2, transport.Calls);
        // A text search reuses the short names the batch read: one request for the query, none for the projects.
        Assert.Single((await admin.GetFromJsonAsync<YouTrackIssue[]>($"/api/integrations/youtrack/search?spaceId={space}&q=work"))!);
        Assert.Equal(3, transport.Calls);

        // A database ID and a key with a project's former short name are not the allowed projects' keys: the batch does not ask
        // YouTrack about them and answers null, but leaves no null in the cache that would make resolving them alone a 404.
        var unasked = await Read<YouTrackIssue?[]>(await Send(admin, HttpMethod.Post, resolve, new YouTrackResolveRequest(["TEAM-5", "2-1", "OLD-1"])));
        Assert.All(unasked, Assert.Null);
        Assert.Equal(4, transport.Calls); // one search for TEAM-5 alone
        Assert.Equal("TEAM-1", (await Read<YouTrackIssue>(await admin.GetAsync($"{resolve}&reference=2-1"))).Key);
        Assert.Equal("TEAM-1", (await Read<YouTrackIssue>(await admin.GetAsync($"{resolve}&reference=old-1"))).Key);
        Assert.Equal(6, transport.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{resolve}&reference=TEAM-5")).StatusCode);
        Assert.Equal(6, transport.Calls);
    }

    [PostgreSqlFact]
    public async Task Resolving_fifty_references_runs_as_many_sql_statements_as_resolving_one()
    {
        // GAP4-7: a pasted set is one authorization before and one after the YouTrack call, however many references it holds.
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        using var admin = site.Client(); await Setup(site, admin);
        var space = await NewSpace(admin);
        await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(space, ["0-7"])], null)));
        var resolve = $"/api/integrations/youtrack/resolve?spaceId={space}";
        // Warm the pool and the per-process caches so that both measured requests start alike; a batch also reads and stores the short
        // names, which a fresh read does once per configuration and space.
        await Read<YouTrackIssue?[]>(await Send(admin, HttpMethod.Post, resolve, new YouTrackResolveRequest(["TEAM-100", "TEAM-101"])));
        using var one = await Send(admin, HttpMethod.Post, resolve, new YouTrackResolveRequest(["TEAM-1"]));
        using var fifty = await Send(admin, HttpMethod.Post, resolve, new YouTrackResolveRequest(Enumerable.Range(2, 50).Select(i => $"TEAM-{i}").ToArray()));
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(50, (await Read<YouTrackIssue?[]>(fifty)).Length);
        Assert.Equal(4, transport.Calls); // the short names and a search for TEAM-100 and TEAM-101, TEAM-1, one search for the fifty
        var statements = Statements(one);
        Assert.NotNull(statements);
        Assert.Equal(statements, Statements(fifty));
    }

    [PostgreSqlFact]
    public async Task The_lexicon_lists_the_stored_short_names_of_the_space_projects_only_for_its_readers()
    {
        // P15: a typed issue key is recognised from the lexicon, which never asks YouTrack; the names come from YouTrack's own answers.
        var transport = new Upstream();
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_yt_",
            settings =>
            {
                settings["YouTrack:Enabled"] = "true";
                settings["YouTrack:BaseUrl"] = "";
                settings["YouTrack:Token"] = "";
            },
            services => services.AddHttpClient<YouTrackClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        using var admin = site.Client(); await Setup(site, admin);
        var space = await NewSpace(admin);
        var other = await NewSpace(admin);
        async Task<string[]> Keys(HttpClient client, Guid id) =>
            (await client.GetFromJsonAsync<JsonElement>($"/api/spaces/{id}/lexicon")).GetProperty("youTrackProjects").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var saved = await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(space, ["0-7"]), new(other, ["0-9"])], null)));
        Assert.Empty(await Keys(admin, space));
        // A background read of the space's projects stores what YouTrack named; the lexicon itself never calls YouTrack.
        Assert.Single((await admin.GetFromJsonAsync<YouTrackIssue[]>($"/api/integrations/youtrack/search?spaceId={space}&q=work"))!);
        var calls = transport.Calls;
        Assert.Equal(["TEAM"], await Keys(admin, space));
        Assert.Empty(await Keys(admin, other)); // 0-9 has no name YouTrack answered
        Assert.Equal(calls, transport.Calls);

        // A renamed project: "Test spojení" stores the new name and the next save carries it to the new configuration.
        transport.ShortName = "PX2";
        Assert.Equal(HttpStatusCode.OK, (await Send(admin, HttpMethod.Post, Endpoint + "/test")).StatusCode);
        Assert.Equal(["PX2"], await Keys(admin, space));
        var resaved = await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://tracker.example", null, false, [new(space, ["0-7"])], saved.Version)));
        Assert.Equal(["PX2"], await Keys(admin, space));

        // Only a reader of the space gets them (the YouTrack reads' own rule): without a grant the lexicon is a 404.
        var created = await Read<CreatedBuiltinUser>(await Send(admin, HttpMethod.Post, "/api/admin/users", new { username = "reader", displayName = "Reader" }));
        using var reader = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(reader, HttpMethod.Post, "/api/auth/activate", new { token = created.Token, newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(reader, HttpMethod.Post, "/api/auth/login", new { username = "reader", password = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/spaces/{space}/lexicon")).StatusCode);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new UpdateSpaceGrant(created.User.Id, null, "Reader", null));
        Assert.Equal(["PX2"], await Keys(reader, space));

        // Another server has other projects; a switched-off integration has none.
        var moved = await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(true, "https://another.example", SyntheticToken, false, [new(space, ["0-7"])], resaved.Version)));
        Assert.Empty(await Keys(admin, space));
        Assert.Equal(HttpStatusCode.OK, (await Send(admin, HttpMethod.Post, Endpoint + "/test")).StatusCode);
        Assert.Equal(["PX2"], await Keys(admin, space));
        await Read<YouTrackSettingsDto>(await Send(admin, HttpMethod.Put, Endpoint, new SaveYouTrackSettingsRequest(false, "https://another.example", null, false, [new(space, ["0-7"])], moved.Version)));
        Assert.Empty(await Keys(reader, space));
    }

    private static int? Statements(HttpResponseMessage response) =>
        Server.Platform.Hosting.AtlasServerTiming.Statements(string.Join(",", response.Headers.GetValues("Server-Timing")));

    private static async Task<T> Read<T>(HttpResponseMessage response) { Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}"); return (await response.Content.ReadFromJsonAsync<T>())!; }
    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object? body = null) => AtlasHttpSite.SendAsync(client, method, url, body);
    private static async Task Setup(AtlasHttpSite site, HttpClient client) => Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/setup/complete", new { bootstrapToken = site.BootstrapToken, workspaceName = "YouTrack acceptance", username = "administrator", displayName = "Administrator", password = site.Password })).StatusCode);
    private static async Task<Guid> NewSpace(HttpClient client) => (await Read<JsonElement>(await Send(client, HttpMethod.Post, "/api/spaces", new { name = "Engineering" }))).GetProperty("id").GetGuid();

    private sealed class Upstream : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold;
        public Func<Task>? OnRequest;
        public string ShortName = "TEAM";
        public readonly ConcurrentQueue<string?> Authorizations = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Started.TrySetResult();
            Authorizations.Enqueue(request.Headers.Authorization?.ToString());
            if (OnRequest is not null)
            {
                await OnRequest();
            }
            if (Hold is not null)
            {
                await Hold.Task.WaitAsync(ct);
            }
            const string issue = """{"id":"0-issue","idReadable":"TEAM-1","summary":"Allowed fixture","project":{"id":"0-7"},"customFields":[]}""";
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/admin/projects/0-7", StringComparison.Ordinal) ? $$"""{"id":"0-7","shortName":"{{ShortName}}"}""" : path.Contains("admin/projects") ? $$"""[{"id":"0-7","name":"Team","shortName":"{{ShortName}}"}]""" : path.EndsWith("/issues") ? "[" + issue + "]" : issue;
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
