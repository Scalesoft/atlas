using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Jobs;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Jobs;

/// <summary>What the job queries and commands decide beyond the rights (the matrix's): what the list shows and hides, what a run reveals to whom,
/// which runs may be retried or stopped, and who the control hears pressed the button. The routes are only the carrier.</summary>
public sealed class JobHandlerTests
{
    private static async Task<JsonNode> GetAsync(HttpClient client, string path) => await AtlasHttpSite.Json(await client.GetAsync(path), HttpStatusCode.OK);

    private static async Task<string> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["code"]!.GetValue<string>();
    }

    [PostgreSqlFact]
    public async Task The_list_names_every_job_with_its_newest_run_and_hides_what_the_viewer_may_not_see()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var jobs = (await GetAsync(site.F.Admin, "/api/admin/jobs"))["jobs"]!.AsArray().ToDictionary(job => job!["id"]!.GetValue<string>(), job => job!);

        Assert.Equal("Každou hodinu", jobs["test.platform"]["schedule"]!.GetValue<string>());
        Assert.Equal("Po události nebo ručně", jobs["test.space"]["schedule"]!.GetValue<string>());
        Assert.Equal(site.ForeignFailed, jobs["test.space"]["lastRun"]!["id"]!.GetValue<long>());
        // A row whose definition is gone keeps its history as „Neznámá úloha“; a job of a module this host does not run is not listed at all.
        Assert.False(jobs["test.gone"]["known"]!.GetValue<bool>());
        Assert.Equal("Neznámá úloha", jobs["test.gone"]["title"]!.GetValue<string>());
        Assert.DoesNotContain("test.projector", jobs.Keys);

        // A space administrator gets the jobs with runs of their spaces, and in them only those runs.
        var (admin, _, _, _) = await site.UserAsync("spravce", site.Own, "Admin");
        var seen = (await GetAsync(admin, "/api/admin/jobs"))["jobs"]!.AsArray();
        Assert.Equal(new[] { "test.space" }, seen.Select(job => job!["id"]!.GetValue<string>()));
        Assert.Equal(site.OwnQueued, seen[0]!["lastRun"]!["id"]!.GetValue<long>());
        var history = await GetAsync(admin, "/api/admin/jobs/test.space/runs");
        Assert.Equal(new[] { site.OwnQueued, site.OwnFailed }, history["runs"]!.AsArray().Select(run => run!["id"]!.GetValue<long>()));
        Assert.Equal(2, history["total"]!.GetValue<int>());
    }

    [PostgreSqlFact]
    public async Task A_run_shows_its_summary_and_log_and_a_space_administrator_sees_only_the_first_line_of_its_error()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var path = $"/api/admin/jobs/test.space/runs/{site.OwnFailed}";

        var run = await GetAsync(site.F.Admin, path);
        Assert.Equal(3, run["run"]!["summary"]!["pages"]!.GetValue<int>());
        Assert.Equal(new[] { "Information", "Warning" }, run["log"]!.AsArray().Select(entry => entry!["level"]!.GetValue<string>()));
        Assert.Equal("Stránka 7 se přeskočila", run["log"]![1]!["message"]!.GetValue<string>());
        Assert.Contains("at Atlas.Jobs.Own()", run["error"]!.GetValue<string>());
        Assert.False(run["run"]!.AsObject().ContainsKey("progress"), "the checkpoint is the job's, not the viewer's");

        var (admin, _, _, _) = await site.UserAsync("spravce", site.Own, "Admin");
        Assert.Equal("System.InvalidOperationException: nic", (await GetAsync(admin, path))["error"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Retry_and_cancel_refuse_a_run_in_the_wrong_state_and_run_refuses_a_job_that_is_gone()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var admin = site.F.Admin;
        Task<HttpResponseMessage> Post(string path) => AtlasHttpSite.SendAsync(admin, HttpMethod.Post, path);

        Assert.Equal("run_not_retryable", await ProblemAsync(await Post($"/api/admin/jobs/test.space/runs/{site.OwnQueued}/retry"), HttpStatusCode.Conflict));
        Assert.Equal("run_finished", await ProblemAsync(await Post($"/api/admin/jobs/test.space/runs/{site.OwnFailed}/cancel"), HttpStatusCode.Conflict));
        Assert.Equal("job_unknown", await ProblemAsync(await Post("/api/admin/jobs/test.gone/runs"), HttpStatusCode.Conflict));
        // A run is addressed through its own job, and a job that does not exist is not found.
        Assert.Equal("not_found", await ProblemAsync(await Post($"/api/admin/jobs/test.platform/runs/{site.OwnFailed}/retry"), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(await Post("/api/admin/jobs/nope/runs"), HttpStatusCode.NotFound));
        Assert.Empty(site.Recorder.Calls);
    }

    [PostgreSqlFact]
    public async Task The_control_hears_who_pressed_the_button_and_what()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var admin = site.F.Admin;

        var started = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/jobs/test.space/runs"), HttpStatusCode.OK);
        Assert.Equal(900, started["runId"]!.GetValue<long>());
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/admin/jobs/test.space/runs/{site.OwnFailed}/retry")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/jobs/test.space/paused", new { paused = true })).StatusCode);

        Assert.Equal(new[] { "run test.space Spustil(a) Administrator", $"retry {site.OwnFailed}", "pause test.space True" }, site.Recorder.Calls);
    }

    /// <summary>The same buttons on the job runtime's own control (core's): a run queued by hand, stopped, tried again, and the pause.</summary>
    [PostgreSqlFact]
    public async Task The_job_runtimes_control_queues_stops_retries_and_pauses_what_the_handlers_ask_for()
    {
        await using var site = await JobsTestSite.CreateAsync(recorded: false);
        var admin = site.F.Admin;
        async Task<string> State(long id) => (string)(await site.F.Site.ScalarAsync($"""SELECT "State" || '|' || "Trigger" || '|' || ("RequestedBy" IS NOT NULL) || '|' || coalesce("Reason", '') FROM "JobRun" WHERE "Id" = {id}"""))!;

        var started = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/jobs/test.platform/runs"), HttpStatusCode.OK))["runId"]!.GetValue<long>();
        Assert.Equal("queued|manual|true|Spustil(a) Administrator", await State(started));

        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/admin/jobs/test.platform/runs/{started}/cancel")).StatusCode);
        Assert.StartsWith("cancelled|", await State(started));
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/admin/jobs/test.platform/runs/{started}/retry")).StatusCode);
        Assert.StartsWith("queued|retry|", await State(started));
        var merged = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/admin/jobs/test.platform/runs/{site.PlatformFailed}/retry"), HttpStatusCode.OK))["runId"]!.GetValue<long>();
        // A run of the same job already queued takes the retry: the failed one stays failed, and the answer names the run that will go (the page opens it).
        Assert.StartsWith("failed|", await State(site.PlatformFailed));
        Assert.Equal(started, merged);

        // A space administrator stops a queued run of their own space through the same control.
        var (spaceAdmin, _, _, _) = await site.UserAsync("spravce", site.Own, "Admin");
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(spaceAdmin, HttpMethod.Post, $"/api/admin/jobs/test.space/runs/{site.OwnQueued}/cancel")).StatusCode);
        Assert.StartsWith("cancelled|", await State(site.OwnQueued));

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/jobs/test.space/paused", new { paused = true })).StatusCode);
        Assert.Equal("true|true", await site.F.Site.ScalarAsync("""SELECT "Paused" || '|' || ("PausedBy" IS NOT NULL) FROM "Job" WHERE "Id" = 'test.space'"""));
        var listed = (await GetAsync(admin, "/api/admin/jobs"))["jobs"]!.AsArray().Single(job => job!["id"]!.GetValue<string>() == "test.space")!;
        Assert.True(listed["paused"]!.GetValue<bool>());
        // The run page learns it too, so it does not poll for a queued run that waits for the resume.
        Assert.True((await GetAsync(admin, $"/api/admin/jobs/test.space/runs/{site.OwnQueued}"))["paused"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/jobs/test.space/paused", new { paused = false })).StatusCode);
        Assert.False((await GetAsync(admin, "/api/admin/jobs"))["jobs"]!.AsArray().Single(job => job!["id"]!.GetValue<string>() == "test.space")!["paused"]!.GetValue<bool>());
    }

    [PostgreSqlFact]
    public async Task The_system_services_are_listed_for_the_wiki_administrator_with_the_outbox_counts()
    {
        await using var site = await JobsTestSite.CreateAsync();

        var services = (await GetAsync(site.F.Admin, "/api/admin/jobs"))["systemServices"]!.AsArray().ToDictionary(item => item!["id"]!.GetValue<string>(), item => item!);

        Assert.Equal(SystemServices.All.Select(info => info.Id).Order(), services.Keys.Order());
        Assert.Equal("Čeká 0, mrtvé 0", services["outbox"]["detail"]!.GetValue<string>());
        Assert.Equal("ok", services["outbox"]["state"]!.GetValue<string>());
    }

    [Fact]
    public void A_service_that_has_not_beaten_for_three_intervals_does_not_respond()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var loop = SystemServices.All.Single(info => info.Id == SystemServices.LiveRefresh);
        var once = SystemServices.All.Single(info => info.Id == SystemServices.DatabaseInitializer);

        Assert.Equal("ok", SystemServices.View(loop, new SystemServiceBeat(now.AddSeconds(-14), null), now).State);
        Assert.Equal("unresponsive", SystemServices.View(loop, new SystemServiceBeat(now.AddSeconds(-16), null), now).State);
        var failed = SystemServices.View(loop, new SystemServiceBeat(now.AddSeconds(-2), "boom"), now);
        Assert.Equal(("failed", "boom"), (failed.State, failed.Error));
        // A loop with no pass to wait for is never late; before its first beat it is only waiting.
        Assert.Equal("waiting", SystemServices.View(once, null, now).State);
        Assert.Equal("ok", SystemServices.View(once, new SystemServiceBeat(now.AddDays(-30), null), now).State);
    }
}
