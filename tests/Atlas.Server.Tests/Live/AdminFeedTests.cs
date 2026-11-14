using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Logs;
using Atlas.Server.Tests.Jobs;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Inbox = Atlas.Server.Tests.Boards.BoardLiveHttpTests.Inbox;

namespace Atlas.Server.Tests.Live;

/// <summary>A job of these tests that fails at once, without a retry.</summary>
public sealed class LiveFailingJob : IJobDefinition
{
    public string Id => "test.live-failing";
    public string Title => "Zkušební úloha, která selže";
    public string Description => "Selže hned.";
    public Task RunAsync(JobContext context, CancellationToken ct) => throw new JobFailedException("selhalo");
}

/// <summary>
/// The jobs and log feeds of /hubs/live (3.6: no polling): who may subscribe them (the server derives the channels, deny by default), which
/// events reach whom (a space administrator only their space's runs, logs only the effective wiki administrators), a revocation mid-stream,
/// the per-run throttle and the SubscribeAll budget.
/// </summary>
public sealed class AdminFeedTests
{
    internal static readonly Uri s_base = new("https://localhost");

    [Fact]
    public async Task The_throttle_sends_the_first_signal_at_once_and_a_burst_once_at_the_end_of_the_interval()
    {
        var clock = new FakeTimeProvider();
        var throttle = new LiveThrottle(clock, NullLogger<LiveThrottle>.Instance);
        var sent = new List<string>();
        void Post(string key, string value) => throttle.Post(key, () => { lock (sent) sent.Add(value); return Task.CompletedTask; });

        Post("run:1", "a");
        Post("run:1", "b");
        Post("run:1", "c");
        Post("run:2", "x");
        Assert.Equal(["a", "x"], sent);
        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal(["a", "x"], sent);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await WaitAsync(() => sent.Count == 3);
        // The latest of the burst, once.
        Assert.Equal(["a", "x", "c"], sent);
        clock.Advance(TimeSpan.FromSeconds(1));
        Post("run:1", "d");
        Assert.Equal(["a", "x", "c", "d"], sent);
    }

    [Fact]
    public void A_connection_may_subscribe_the_feeds_a_few_times_a_second()
    {
        var clock = new FakeTimeProvider();
        var subscriptions = new LiveSubscriptions(clock);
        subscriptions.Connect(new LiveSubscriber("c1", Guid.NewGuid(), "A", "stamp", null));
        Assert.All(Enumerable.Range(0, LiveSubscriptions.FeedsPerSecond), _ => Assert.True(subscriptions.TryTakeFeed("c1")));
        Assert.False(subscriptions.TryTakeFeed("c1"));
        // Presence keeps its own budget.
        Assert.True(subscriptions.TryTakePresence("c1"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(subscriptions.TryTakeFeed("c1"));
        Assert.False(subscriptions.TryTakeFeed("unknown"));
    }

    [PostgreSqlFact]
    public async Task Each_actor_hears_exactly_the_runs_and_log_lines_its_rights_show()
    {
        await using var site = await JobsTestSite.CreateAsync(more: services => services.AddJob<LiveFailingJob>());
        var f = site.F;
        var own = Guid.Parse(site.Own);
        var foreign = Guid.Parse(site.Foreign);
        var ownAdmin = await site.UserAsync("spravce", site.Own, "Admin");
        await site.UserAsync("spravce-jinde", site.Foreign, "Admin");
        await site.UserAsync("ctenar", site.Own, "Reader");
        await site.UserAsync("nikdo", null, null);
        var hubAdmin = await site.UserAsync("hub-spravce", null, null);
        var sessions = new Dictionary<string, CookieContainer>();
        foreach (var (name, username) in new[] { ("wiki", "administrator"), ("hub", f.LoginFor("hub-spravce")), ("own", f.LoginFor("spravce")),
            ("foreign", f.LoginFor("spravce-jinde")), ("reader", f.LoginFor("ctenar")), ("plain", f.LoginFor("nikdo")) })
        {
            sessions[name] = await SignInAsync(f.Site, username);
        }
        await SeedHubAdminAsync(f.Site, hubAdmin.Id);

        var heard = new Inbox();
        var hubs = new Dictionary<string, HubConnection>();
        foreach (var (name, cookies) in sessions)
        {
            var hub = Connect(f.Site, cookies);
            foreach (var method in new[] { JobLiveChannel.Event, LogLiveChannel.Event, "Revoked" })
            {
                hub.On<JsonElement>(method, message => heard.Add($"{name}:{method}", message));
            }
            await hub.StartAsync();
            hubs[name] = hub;
        }
        try
        {
            // Anonymous (a share link's visitor has no session either) and an API token never get a connection: the hub is the cookie session's.
            var (readToken, _) = await site.AdminTokenAsync("read");
            await Assert.ThrowsAsync<HttpRequestException>(() => Connect(f.Site, new CookieContainer()).StartAsync());
            var token = readToken.DefaultRequestHeaders.Authorization!.ToString();
            await Assert.ThrowsAsync<HttpRequestException>(() => Connect(f.Site, new CookieContainer(), token).StartAsync());

            // Who may subscribe what: decided by the server, deny by default; a client can never name a feed's channel.
            var expected = new Dictionary<string, (string Jobs, string Logs)>
            {
                ["wiki"] = ("ok", "ok"), ["hub"] = ("ok", "ok"), ["own"] = ("ok", "forbidden"), ["foreign"] = ("ok", "forbidden"),
                ["reader"] = ("forbidden", "forbidden"), ["plain"] = ("forbidden", "forbidden"),
            };
            foreach (var (name, (jobs, logs)) in expected)
            {
                Assert.Equal((name, jobs, logs), (name, await AnswerAsync(hubs[name], "jobs"), await AnswerAsync(hubs[name], "logs")));
            }
            await Boards.BoardLiveHttpTests.AssertHubErrorAsync("unsupported", () => hubs["own"].InvokeAsync<LiveSubscribed>("Subscribe", JobLiveChannel.Name(foreign)));
            await Boards.BoardLiveHttpTests.AssertHubErrorAsync("unsupported", () => hubs["reader"].InvokeAsync<LiveSubscribed>("Subscribe", JobLiveChannel.Name(Guid.Empty)));
            await Boards.BoardLiveHttpTests.AssertHubErrorAsync("unsupported", () => hubs["own"].InvokeAsync<LiveSubscribed>("SubscribeAll", "user"));

            // Runs: a space's to its administrators and the wiki's, a global one to the wiki's only; nothing but ids and the state.
            var relay = f.Site.Services.GetRequiredService<LiveRelay>();
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-1, "test.space", "running", own));
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-2, "test.space", "running", foreign));
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-3, "test.platform", "running", null));
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-4, "test.space", "succeeded", own));
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-5, "test.space", "succeeded", foreign));
            Assert.Equal(new long[] { -1, -4 }, await RunsAsync(heard, "own", 2));
            Assert.Equal(new long[] { -2, -5 }, await RunsAsync(heard, "foreign", 2));
            foreach (var admin in new[] { "wiki", "hub" })
            {
                Assert.Equal(new long[] { -1, -2, -3, -4, -5 }, await RunsAsync(heard, admin, 5, test: true));
            }

            // Real runs tell their start and end after the commits: to the wiki's administrators (global runs).
            var runner = f.Site.Services.GetRequiredService<IJobRunner>();
            var succeeded = await runner.RunToEndAsync("test.platform", "test");
            var failed = await runner.RunToEndAsync("test.live-failing", "test");
            foreach (var admin in new[] { "wiki", "hub" })
            {
                var states = await StatesAsync(heard, admin, succeeded, failed);
                Assert.Equal(["running", "succeeded"], states[succeeded]);
                Assert.Equal(["running", "failed"], states[failed]);
            }

            // Stored log lines: a signal without a line, to the wiki's administrators only.
            // The test site's loggers are its own (AtlasHttpSite), so the line goes to the queue the provider fills.
            f.Site.Services.GetRequiredService<AppLogQueue>().Write(new AppLogEntry(DateTime.UtcNow, LogLevel.Warning, "Atlas.Tests.Live", null, "Živý řádek",
                null, null, null));
            foreach (var admin in new[] { "wiki", "hub" })
            {
                var written = await heard.NextAsync($"{admin}:{LogLiveChannel.Event}");
                Assert.Equal(["level"], written.EnumerateObject().Select(property => property.Name));
            }

            // The space administrator loses the role mid-stream: told at once (the change's commit sweeps), and hears nothing more.
            var access = (await f.Admin.GetFromJsonAsync<SpaceAccessSummary>($"/api/spaces/{site.Own}/access"))!;
            var grant = Assert.Single(access.Grants, item => item.UserId == Guid.Parse(ownAdmin.Id));
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/spaces/{site.Own}/access",
                new { userId = ownAdmin.Id, role = (string?)null, expectedVersion = grant.Version }, await AtlasHttpSite.ReauthAsync(f.Admin, f.Site.Password))).StatusCode);
            Assert.Equal("jobs", (await heard.NextAsync("own:Revoked")).GetProperty("channel").GetString());
            await AssertHubErrorAsync("forbidden", hubs["own"], "jobs");
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-6, "test.space", "running", own));
            Assert.Equal(new long[] { -6 }, await RunsAsync(heard, "wiki", 1, test: true));
            await Task.Delay(500);

            // Nobody heard what their rights do not show.
            var everything = new[] { "own", "foreign", "reader", "plain" }.ToDictionary(name => name, name => heard.Count($"{name}:{JobLiveChannel.Event}"));
            Assert.Equal(new Dictionary<string, int> { ["own"] = 2, ["foreign"] = 2, ["reader"] = 0, ["plain"] = 0 }, everything);
            Assert.All(new[] { "own", "foreign", "reader", "plain" }, name => Assert.Equal(0, heard.Count($"{name}:{LogLiveChannel.Event}")));
            Assert.All(new[] { "foreign", "reader", "plain", "wiki", "hub" }, name => Assert.Equal(0, heard.Count($"{name}:Revoked")));
        }
        finally
        {
            foreach (var hub in hubs.Values)
            {
                await hub.DisposeAsync();
            }
        }
    }

    internal static async Task<string> AnswerAsync(HubConnection hub, string kind)
    {
        try
        {
            await hub.InvokeAsync<LiveSubscribed>("SubscribeAll", kind);
            return "ok";
        }
        catch (Microsoft.AspNetCore.SignalR.HubException refused)
        {
            return refused.Message[(refused.Message.LastIndexOf(' ') + 1)..];
        }
    }

    private static async Task AssertHubErrorAsync(string code, HubConnection hub, string kind)
    {
        Assert.Equal(code, await AnswerAsync(hub, kind));
    }

    /// <summary>The next <paramref name="count"/> run ids the actor heard; with <paramref name="test"/> only the ids these tests published
    /// (negative), since the site's own jobs run meanwhile.</summary>
    internal static async Task<long[]> RunsAsync(Inbox heard, string actor, int count, bool test = false)
    {
        var runs = new List<long>();
        while (runs.Count < count)
        {
            var message = await heard.NextAsync($"{actor}:{JobLiveChannel.Event}");
            // Ids and the state only: no error, log, summary or title.
            Assert.Equal(["jobId", "runId", "spaceId", "state"], message.EnumerateObject().Select(property => property.Name).Order());
            var id = message.GetProperty("runId").GetInt64();
            if (!test || id < 0)
            {
                runs.Add(id);
            }
        }
        return [.. runs];
    }

    /// <summary>The first two states the actor heard of each run, skipping other runs (the site's own jobs run meanwhile).</summary>
    private static async Task<Dictionary<long, List<string>>> StatesAsync(Inbox heard, string actor, params long[] runIds)
    {
        var states = runIds.ToDictionary(id => id, _ => new List<string>());
        while (states.Values.Any(list => list.Count < 2))
        {
            var message = await heard.NextAsync($"{actor}:{JobLiveChannel.Event}");
            if (states.TryGetValue(message.GetProperty("runId").GetInt64(), out var list))
            {
                list.Add(message.GetProperty("state").GetString()!);
            }
        }
        return states;
    }

    /// <summary>A Hub administrator: an account whose Hub group grants wiki administration (as HubEffectiveAuthorityTests seeds it), with
    /// IsWikiAdmin left false.</summary>
    internal static Task SeedHubAdminAsync(AtlasHttpSite site, string userId) => site.ExecuteAsync("""
        INSERT INTO "UserGroup"("Id","Name") VALUES (@group,'Hub admins');
        INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom") VALUES (gen_random_uuid(),@group,@user,now());
        INSERT INTO "HubConnection"("Id") VALUES (@connection);
        INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
        VALUES (gen_random_uuid(),@connection,'https://hub.example.test/hub','https://hub.example.test/hub','test-protected',2,@user,statement_timestamp()-interval '1 hour');
        INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
        VALUES (@identity,@user,'https://hub.example.test/hub','opaque-user',statement_timestamp()-interval '1 hour');
        INSERT INTO "HubGroupBinding"("GroupId","ConnectionId","ExternalGroupId","GrantsWikiAdmin") VALUES (@group,@connection,'opaque-group',true);
        INSERT INTO "HubIdentityState"("ExternalIdentityId","UserId","ConnectionId","ExternalUserId","SnapshotStartedAt","VerifiedUntil","LastSuccessAt","LastAttemptAt","UpstreamAccountState","Generation")
        VALUES (@identity,@user,@connection,'opaque-user',statement_timestamp(),statement_timestamp()+interval '5 minutes',statement_timestamp(),statement_timestamp(),1,1);
        """, ("group", Guid.NewGuid()), ("connection", Guid.NewGuid()), ("identity", Guid.NewGuid()), ("user", Guid.Parse(userId)));

    /// <summary>A fresh cookie session of the account, for the hub's handshake (a test client keeps its cookies to itself). The account
    /// is named by its login or, for one McpSite.UserAsync made (a unique login), by its display name: the site is the test's own.</summary>
    internal static async Task<CookieContainer> SignInAsync(AtlasHttpSite site, string name)
    {
        var username = (string)(await site.ScalarAsync("""SELECT "Username" FROM "UserAccount" WHERE "Username" = @name OR "DisplayName" = @name""", ("name", name)))!;
        var cookies = new CookieContainer();
        using var client = site.CreateDefaultClient(s_base, new CookieContainerHandler(cookies));
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login", new { username, password = site.Password })).StatusCode);
        return cookies;
    }

    internal static HubConnection Connect(AtlasHttpSite site, CookieContainer cookies, string? authorization = null)
    {
        var hub = new Uri(s_base, "/hubs/live");
        return new HubConnectionBuilder()
            .WithUrl(hub, options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => site.Server.CreateHandler();
                if (cookies.GetCookieHeader(hub) is { Length: > 0 } cookie)
                {
                    options.Headers["Cookie"] = cookie;
                }
                if (authorization is not null)
                {
                    options.Headers["Authorization"] = authorization;
                }
            })
            .Build();
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(done());
    }
}
