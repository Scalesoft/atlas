using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.PagePdfExports;
using Atlas.Server.Features.Pages.Live;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Logs;
using Atlas.Server.Tests.Jobs;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Atlas.Server.Tests.Live.AdminFeedTests;
using static Atlas.Server.Tests.Mcp.McpSite;
using Inbox = Atlas.Server.Tests.Boards.BoardLiveHttpTests.Inbox;

namespace Atlas.Server.Tests.Live;

/// <summary>
/// The rights of the server-derived feeds beyond the matrix of <see cref="AdminFeedTests"/>: the cost of deciding many spaces, every way a
/// right ends mid-stream (a commit's sweep at once, an expiry by the next periodic sweep), what the handshake refuses, and the PDF export's
/// signal, which only the page's editors hear.
/// </summary>
public sealed class AdminFeedRightsTests
{
    [PostgreSqlFact]
    public async Task An_admin_of_twenty_spaces_is_decided_with_as_many_queries_as_an_admin_of_one()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var f = site.F;
        var one = await site.UserAsync("jeden", site.Own, "Admin");
        var many = await site.UserAsync("dvacet", site.Own, "Admin");
        var spaces = new List<string> { site.Own };
        for (var i = 1; i < 20; i++)
        {
            spaces.Add(await f.SpaceAsync($"Prostor {i}"));
            await GrantAdminAsync(f, spaces[^1], many.Id);
        }
        var channel = f.Site.Services.GetServices<ILiveChannel>().OfType<JobLiveChannel>().Single();

        // What SubscribeAll and a sweep ask of the channel: the derivation, then one decision for all the channels.
        async Task<(int Statements, IReadOnlyList<Guid> Spaces)> CostAsync(string userId)
        {
            var stamp = (string)(await f.Site.ScalarAsync("""SELECT "SecurityStamp" FROM "UserAccount" WHERE "Id"=@id""", ("id", Guid.Parse(userId))))!;
            var subscriber = new LiveSubscriber("c", Guid.Parse(userId), userId, stamp, Guid.NewGuid());
            using var recording = SqlRecording.Start();
            var resources = await channel.ResourcesAsync(subscriber, CancellationToken.None);
            var decisions = await channel.AuthorizeManyAsync(subscriber, resources, CancellationToken.None);
            Assert.All(decisions.Values, access => Assert.True(access.Allowed));
            return (recording.Statements.Count, resources);
        }
        var single = await CostAsync(one.Id);
        var twenty = await CostAsync(many.Id);
        Assert.Single(single.Spaces);
        Assert.Equal(spaces.Select(Guid.Parse).Order(), twenty.Spaces.Order());
        Assert.Equal(single.Statements, twenty.Statements);
        Assert.InRange(twenty.Statements, 1, 10);

        // Over the hub: subscribed to all twenty, it hears a run of the last one.
        var heard = new Inbox();
        await using var hub = Connect(f.Site, await SignInAsync(f.Site, f.LoginFor("dvacet")));
        hub.On<JsonElement>(JobLiveChannel.Event, message => heard.Add("dvacet:" + JobLiveChannel.Event, message));
        await hub.StartAsync();
        Assert.Equal("ok", await AnswerAsync(hub, "jobs"));
        await JobLiveChannel.PublishAsync(f.Site.Services.GetRequiredService<LiveRelay>(), new JobRunChanged(-20, "test.space", "running", Guid.Parse(spaces[^1])));
        Assert.Equal(new long[] { -20 }, await RunsAsync(heard, "dvacet", 1, test: true));
    }

    [PostgreSqlFact]
    public async Task Rights_lost_mid_stream_end_the_feed_at_the_change_or_by_the_next_sweep()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var f = site.F;
        var own = Guid.Parse(site.Own);
        var foreign = Guid.Parse(site.Foreign);
        var both = await site.UserAsync("oba", site.Own, "Admin");
        await GrantAdminAsync(f, site.Foreign, both.Id);
        var demoted = await site.UserAsync("degradovany", null, null);
        await SetUserAsync(f, demoted.Id, active: true, wikiAdmin: true);
        var hubAdmin = await site.UserAsync("hub-spravce", null, null);
        var disabled = await site.UserAsync("vypnuty", site.Own, "Admin");
        await site.UserAsync("odchazejici", site.Own, "Admin");
        var expiring = await site.UserAsync("docasny", site.Own, "Admin");
        var names = new[] { "administrator", "oba", "degradovany", "hub-spravce", "vypnuty", "odchazejici", "docasny" };
        var sessions = new Dictionary<string, CookieContainer>();
        foreach (var name in names)
        {
            sessions[name] = await SignInAsync(f.Site, name == "administrator" ? name : f.LoginFor(name));
        }
        await SeedHubAdminAsync(f.Site, hubAdmin.Id);

        var heard = new Inbox();
        var hubs = new Dictionary<string, HubConnection>();
        try
        {
            foreach (var name in names)
            {
                var hub = Connect(f.Site, sessions[name]);
                foreach (var method in new[] { JobLiveChannel.Event, LogLiveChannel.Event, "Revoked" })
                {
                    hub.On<JsonElement>(method, message => heard.Add($"{name}:{method}", message));
                }
                await hub.StartAsync();
                hubs[name] = hub;
                Assert.Equal("ok", await AnswerAsync(hub, "jobs"));
            }
            Assert.Equal("ok", await AnswerAsync(hubs["degradovany"], "logs"));
            Assert.Equal("ok", await AnswerAsync(hubs["hub-spravce"], "logs"));
            var relay = f.Site.Services.GetRequiredService<LiveRelay>();
            async Task<string> RevokedAsync(string name) => (await heard.NextAsync(name + ":Revoked")).GetProperty("channel").GetString()!;

            // An administrator of A and B loses A: told at once, and hears B's runs but no more of A's.
            await RemoveRoleAsync(f, site.Own, both.Id);
            Assert.Equal("jobs", await RevokedAsync("oba"));
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-1, "test.space", "running", own));
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-2, "test.space", "running", foreign));
            Assert.Equal(new long[] { -2 }, await RunsAsync(heard, "oba", 1, test: true));

            // A demoted wiki administrator and a disabled account: their changes' commits sweep (both feeds of the demoted one).
            await SetUserAsync(f, demoted.Id, active: true, wikiAdmin: false);
            Assert.Equal(new[] { "jobs", "logs" }, new[] { await RevokedAsync("degradovany"), await RevokedAsync("degradovany") }.Order());
            await SetUserAsync(f, disabled.Id, active: false, wikiAdmin: false);
            Assert.Equal("jobs", await RevokedAsync("vypnuty"));

            // Signing out revokes the browser session the connection was opened with.
            using (var leaving = f.Site.CreateDefaultClient(s_base, new CookieContainerHandler(sessions["odchazejici"])))
            {
                Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(leaving, HttpMethod.Post, "/api/auth/logout")).StatusCode);
            }
            Assert.Equal("jobs", await RevokedAsync("odchazejici"));

            // A Hub administrator's verification and a time-limited grant run out without a commit: the periodic sweep (5 s) ends them.
            await f.Site.ExecuteAsync("""
                UPDATE "HubIdentityState" SET "SnapshotStartedAt"=statement_timestamp()-interval '6 minutes', "VerifiedUntil"=statement_timestamp()-interval '1 minute'
                WHERE "UserId"=@user
                """, ("user", Guid.Parse(hubAdmin.Id)));
            // The grant history is append-only: the current interval closes and a copy that expires in 2 s takes its place, with no commit
            // the relay hears.
            await f.Site.ExecuteAsync("""
                WITH closed AS (UPDATE "SpaceGrant" SET "ActiveTo"=statement_timestamp() WHERE "UserId"=@user AND "ActiveTo" IS NULL RETURNING *)
                INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","GroupId","Role","StartAt","ExpiresAt","ActiveFrom")
                SELECT gen_random_uuid(),"SpaceId","UserId","GroupId","Role","StartAt",statement_timestamp()+interval '2 seconds',statement_timestamp() FROM closed
                """, ("user", Guid.Parse(expiring.Id)));
            Assert.Equal(new[] { "jobs", "logs" }, new[] { await RevokedAsync("hub-spravce"), await RevokedAsync("hub-spravce") }.Order());
            Assert.Equal("jobs", await RevokedAsync("docasny"));

            // After all that, only the wiki administrator hears a space's run and a global one.
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-3, "test.space", "running", own));
            await JobLiveChannel.PublishAsync(relay, new JobRunChanged(-4, "test.platform", "running", null));
            Assert.Contains(-4L, await RunsAsync(heard, "administrator", 4, test: true));
            await Task.Delay(500);
            // The runs published before the losses (-1, -2) reached whoever held the right then; after them (-3, -4) nobody but the wiki
            // administrator. The one who lost A heard B's run only.
            var testRuns = names.Skip(1).ToDictionary(name => name, name => heard.All($"{name}:{JobLiveChannel.Event}")
                .Select(message => message.GetProperty("runId").GetInt64()).Where(id => id < 0).ToArray());
            Assert.Equal(new long[] { -2 }, testRuns["oba"]);
            Assert.All(names.Skip(1), name => Assert.DoesNotContain(testRuns[name], id => id is -3 or -4));
            Assert.Equal(0, heard.Count("degradovany:" + LogLiveChannel.Event) + heard.Count("hub-spravce:" + LogLiveChannel.Event));
            // Asked again, the server derives what is left.
            Assert.Equal(("ok", "forbidden", "forbidden", "forbidden"), (await AnswerAsync(hubs["oba"], "jobs"), await AnswerAsync(hubs["degradovany"], "jobs"),
                await AnswerAsync(hubs["hub-spravce"], "logs"), await AnswerAsync(hubs["docasny"], "jobs")));
        }
        finally
        {
            foreach (var hub in hubs.Values)
            {
                await hub.DisposeAsync();
            }
        }
    }

    [PostgreSqlFact]
    public async Task The_hub_refuses_a_trashed_space_a_flood_another_origin_and_a_token_beside_a_cookie()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var f = site.F;
        var trashed = await f.SpaceAsync("Koš");
        await site.UserAsync("spravce-kose", trashed, "Admin");
        await site.UserAsync("ctenar", site.Own, "Reader");
        var trashedSession = await SignInAsync(f.Site, f.LoginFor("spravce-kose"));
        var readerSession = await SignInAsync(f.Site, f.LoginFor("ctenar"));
        var adminSession = await SignInAsync(f.Site, "administrator");
        await f.Site.ExecuteAsync("""UPDATE "Space" SET "TrashedAt"=now(), "TrashedById"=@admin, "TrashBatchId"=gen_random_uuid() WHERE "Id"=@id""",
            ("admin", Guid.Parse(f.AdminId)), ("id", Guid.Parse(trashed)));

        // A space in the trash administers nothing.
        await using (var hub = Connect(f.Site, trashedSession))
        {
            await hub.StartAsync();
            Assert.Equal("forbidden", await AnswerAsync(hub, "jobs"));
        }

        // A flood of SubscribeAll is cut at FeedsPerSecond a second.
        await using (var hub = Connect(f.Site, adminSession))
        {
            await hub.StartAsync();
            var answers = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                answers.Add(await AnswerAsync(hub, "logs"));
            }
            Assert.Contains("rate_limited", answers);
        }

        // Another origin gets no negotiation, even with the session cookie; the same origin does.
        async Task<HttpStatusCode> NegotiateAsync(string origin)
        {
            using var client = f.Site.CreateDefaultClient(s_base, new CookieContainerHandler(adminSession));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/live/negotiate?negotiateVersion=1");
            request.Headers.Add("Origin", origin);
            return (await client.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.Forbidden, await NegotiateAsync("https://evil.example.test"));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync("https://localhost"));

        // A token beside a reader's cookie changes nothing: the hub is the cookie session's, and the reader may hear no feed.
        var (adminToken, _) = await site.AdminTokenAsync("read");
        await using (var hub = Connect(f.Site, readerSession, adminToken.DefaultRequestHeaders.Authorization!.ToString()))
        {
            await hub.StartAsync();
            Assert.Equal(("forbidden", "forbidden"), (await AnswerAsync(hub, "jobs"), await AnswerAsync(hub, "logs")));
        }
    }

    [PostgreSqlFact]
    public async Task Only_the_editors_of_a_page_hear_its_pdf_export()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var f = site.F;
        await site.UserAsync("ctenar", site.Own, "Reader");
        var page = Guid.Parse((await PageAsync(f.Admin, site.Own, "Příručka", Rich(Paragraph("x")), "x")).Id);
        var channel = PageLiveChannel.Name(page);
        var heard = new Inbox();
        await using var editor = Connect(f.Site, await SignInAsync(f.Site, "administrator"));
        await using var reader = Connect(f.Site, await SignInAsync(f.Site, f.LoginFor("ctenar")));
        foreach (var (name, hub) in new[] { ("editor", editor), ("reader", reader) })
        {
            hub.On<JsonElement>(PagePdfExportChanged.Event, message => heard.Add($"{name}:pdf", message));
            hub.On<JsonElement>("Probe", message => heard.Add($"{name}:probe", message));
            await hub.StartAsync();
            await hub.InvokeAsync<LiveSubscribed>("Subscribe", channel);
        }
        var relay = f.Site.Services.GetRequiredService<LiveRelay>();
        await PagePdfExportChanged.PublishAsync(relay, page);
        // A message to every subscriber after it: once the reader has it, the export's signal would have come first.
        await relay.PublishAsync(channel, "Probe", new { });
        Assert.Equal(page, (await heard.NextAsync("editor:pdf")).GetProperty("pageId").GetGuid());
        await heard.NextAsync("reader:probe");
        Assert.Equal(0, heard.Count("reader:pdf"));
    }

    [PostgreSqlFact]
    public async Task Ai_capabilities_reach_every_account_and_ai_health_only_the_wiki_administrators()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var f = site.F;
        await site.UserAsync("ctenar", site.Own, "Reader");
        var heard = new Inbox();
        await using var admin = Connect(f.Site, await SignInAsync(f.Site, "administrator"));
        await using var reader = Connect(f.Site, await SignInAsync(f.Site, f.LoginFor("ctenar")));
        foreach (var (name, hub) in new[] { ("admin", admin), ("reader", reader) })
        {
            hub.On<JsonElement>(Atlas.Server.Features.Ai.Availability.AiLiveChannel.Event, message => heard.Add(name + ":ai", message));
            hub.On<JsonElement>(Atlas.Server.Features.Ai.Availability.AiAdminLiveChannel.Event, message => heard.Add(name + ":health", message));
            await hub.StartAsync();
        }
        Assert.Equal(("ok", "ok"), (await AnswerAsync(admin, "ai"), await AnswerAsync(admin, "ai-admin")));
        Assert.Equal(("ok", "forbidden"), (await AnswerAsync(reader, "ai"), await AnswerAsync(reader, "ai-admin")));

        // A verdict is the administrators' health only; a settings change (the cache's Changed after its commit) is everyone's capabilities.
        f.Site.Services.GetRequiredService<Atlas.Server.Features.Ai.Availability.AiLiveSignals>().HealthChanged();
        Assert.Empty((await heard.NextAsync("admin:health")).EnumerateObject());
        f.Site.Services.GetRequiredService<Atlas.Server.Features.Ai.AiConfigurationCache>().Changed();
        Assert.Empty((await heard.NextAsync("reader:ai")).EnumerateObject());
        await heard.NextAsync("admin:ai");
        await heard.NextAsync("admin:health");
        await Task.Delay(700);
        Assert.Equal((2, 1, 1, 0), (heard.Count("admin:health"), heard.Count("admin:ai"), heard.Count("reader:ai"), heard.Count("reader:health")));
    }

    private static async Task GrantAdminAsync(McpSite f, string space, string userId)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/spaces/{space}/access",
            new { userId, role = "Admin", expectedVersion = (string?)null }, await AtlasHttpSite.ReauthAsync(f.Admin, f.Site.Password))).StatusCode);
    }

    private static async Task RemoveRoleAsync(McpSite f, string space, string userId)
    {
        var access = (await f.Admin.GetFromJsonAsync<SpaceAccessSummary>($"/api/spaces/{space}/access"))!;
        var grant = Assert.Single(access.Grants, item => item.UserId == Guid.Parse(userId));
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/spaces/{space}/access",
            new { userId, role = (string?)null, expectedVersion = grant.Version }, await AtlasHttpSite.ReauthAsync(f.Admin, f.Site.Password))).StatusCode);
    }

    private static async Task SetUserAsync(McpSite f, string userId, bool active, bool wikiAdmin)
    {
        var user = (await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/admin/users"), HttpStatusCode.OK)).AsArray()
            .Single(item => item!["id"]!.GetValue<string>() == userId)!;
        var response = await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/admin/users/{userId}",
            new { displayName = user["displayName"]!.GetValue<string>(), isActive = active, isWikiAdmin = wikiAdmin, expectedVersion = user["version"]!.GetValue<string>() },
            await AtlasHttpSite.ReauthAsync(f.Admin, f.Site.Password));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}
