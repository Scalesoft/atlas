using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using ModelContextProtocol.Client;
using Xunit;
using Xunit.Abstractions;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Jobs;

/// <summary>
/// The rights of background jobs (3.3 plan §4.6): every route of the web's cookie API and of /api/v1 for each kind of actor, then the four
/// MCP tools for the tokens. A wiki administrator does everything; a space administrator sees, retries and cancels the runs of their
/// own space, never runs a job anew (a new run carries no space) or pauses, and sees no system services; readers and guests nothing; a token is its owner within its scope. The control
/// behind the handlers (core's) is recorded, so nothing here depends on the job runtime. "ok" is a 2xx.
/// </summary>
public sealed class JobsRightsMatrixTests(ITestOutputHelper output)
{
    private enum Kind { List, History, Detail, Foreign, Run, Retry, Cancel, Pause, Services }

    private static readonly Kind[] Kinds = Enum.GetValues<Kind>();

    private static Dictionary<Kind, string> Row(params string[] answers) => Kinds.Zip(answers).ToDictionary(pair => pair.First, pair => pair.Second);

    /// <summary>Expected answer per actor and kind: ok, a status, or for the system services "has" and "empty".</summary>
    private static readonly Dictionary<string, Dictionary<Kind, string>> Expected = new()
    {
        ["anonymous"] = Row("401", "401", "401", "401", "401", "401", "401", "401", "401"),
        ["guest of the page"] = Row("403", "403", "403", "403", "403", "403", "403", "403", "403"),
        ["reader"] = Row("403", "403", "403", "403", "403", "403", "403", "403", "403"),
        ["admin of the space"] = Row("ok", "ok", "ok", "404", "403", "ok", "ok", "403", "empty"),
        ["admin of another space"] = Row("ok", "404", "404", "404", "403", "404", "404", "403", "empty"),
        ["wiki admin"] = Row("ok", "ok", "ok", "ok", "ok", "ok", "ok", "ok", "has"),
        ["read token of the wiki admin"] = Row("ok", "ok", "ok", "ok", "403", "403", "403", "403", "has"),
        ["write token of the wiki admin"] = Row("ok", "ok", "ok", "ok", "ok", "ok", "ok", "ok", "has"),
        ["write token of the space admin"] = Row("ok", "ok", "ok", "404", "403", "ok", "ok", "403", "empty"),
    };

    private sealed record Actor(string Name, HttpClient Client, bool Bearer, McpClient? Mcp = null);

    [PostgreSqlFact]
    public async Task Every_route_and_tool_answers_each_actor_as_its_rights_say()
    {
        await using var site = await JobsTestSite.CreateAsync();
        var f = site.F;
        var actors = new List<Actor> { new("anonymous", f.Site.Client(), false) };

        var (guest, guestId) = await f.UserAsync("host");
        var page = await McpSite.PageAsync(f.Admin, site.Own, "Stránka", Rich(Paragraph("x")), "x");
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{page.Id}/access/grants",
            new { userId = guestId, role = "Reader" })).StatusCode);
        actors.Add(new Actor("guest of the page", guest, false));
        actors.Add(new Actor("reader", (await site.UserAsync("ctenar", site.Own, "Reader")).Cookie, false));
        actors.Add(new Actor("admin of the space", (await site.UserAsync("spravce", site.Own, "Admin")).Cookie, false));
        actors.Add(new Actor("admin of another space", (await site.UserAsync("spravce-jinde", await f.SpaceAsync("Jiný prostor"), "Admin")).Cookie, false));
        actors.Add(new Actor("wiki admin", f.Admin, false));
        var (readRest, readMcp) = await site.AdminTokenAsync("read");
        actors.Add(new Actor("read token of the wiki admin", readRest, true, readMcp));
        var (publishRest, publishMcp) = await site.AdminTokenAsync("read", "write");
        actors.Add(new Actor("write token of the wiki admin", publishRest, true, publishMcp));
        var spaceAdmin = await site.UserAsync("spravce-token", site.Own, "Admin", ["read", "write"]);
        actors.Add(new Actor("write token of the space admin", spaceAdmin.Api!, true, spaceAdmin.Mcp));

        var failures = new List<string>();
        foreach (var actor in actors)
        {
            var prefix = actor.Bearer ? "/api/v1/jobs" : "/api/admin/jobs";
            foreach (var kind in Kinds)
            {
                var actual = await AnswerAsync(actor, kind, prefix, site);
                output.WriteLine($"{actor.Name,-34} {kind,-9} {actual}");
                if (actual != Expected[actor.Name][kind])
                {
                    failures.Add($"{actor.Name} · {kind}: expected {Expected[actor.Name][kind]}, got {actual}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // The MCP tools answer the tokens as the routes do; a refused scope is the tool error scope_missing.
        async Task<string> ToolAsync(Actor actor, string tool, object? arguments)
        {
            var result = await CallAsync(actor.Mcp!, tool, arguments);
            return result.IsError == true ? ErrorCode(result) : "ok";
        }
        var own = new { jobId = "test.space", runId = site.OwnFailed };
        var cancel = new { jobId = "test.space", runId = site.OwnQueued };
        var (read, publish, spaceToken) = (actors[6], actors[7], actors[8]);
        Assert.Equal(new[] { "ok", "ok", "scope_missing", "scope_missing" }, new[] { await ToolAsync(read, "atlas_list_jobs", null), await ToolAsync(read, "atlas_get_job_run", own),
            await ToolAsync(read, "atlas_run_job", new { jobId = "test.space" }), await ToolAsync(read, "atlas_cancel_job_run", cancel) });
        Assert.Equal(new[] { "ok", "ok", "ok", "ok" }, new[] { await ToolAsync(publish, "atlas_list_jobs", null), await ToolAsync(publish, "atlas_get_job_run", own),
            await ToolAsync(publish, "atlas_run_job", new { jobId = "test.space", retryRunId = site.OwnFailed }), await ToolAsync(publish, "atlas_cancel_job_run", cancel) });
        Assert.Equal(new[] { "ok", "forbidden", "not_found", "ok" }, new[] { await ToolAsync(spaceToken, "atlas_get_job_run", own), await ToolAsync(spaceToken, "atlas_run_job", new { jobId = "test.space" }),
            await ToolAsync(spaceToken, "atlas_get_job_run", new { jobId = "test.space", runId = site.ForeignFailed }), await ToolAsync(spaceToken, "atlas_cancel_job_run", cancel) });
        // Pausing is the web's and the API's: the tools offer none.
        Assert.DoesNotContain(await publish.Mcp!.ListToolsAsync(), tool => tool.Name.Contains("pause", StringComparison.Ordinal));
        Assert.Contains($"retry {site.OwnFailed}", site.Recorder.Calls);
        Assert.Contains($"cancel {site.OwnQueued}", site.Recorder.Calls);
        // Every attempt of a token is audited, the refused scope included.
        Assert.True((long)(await f.Site.ScalarAsync("""SELECT count(*) FROM "ApiActivity" WHERE "Operation" IN ('job.run','job.retry','job.cancel','job.pause','atlas_run_job','atlas_cancel_job_run')"""))! > 0);
    }

    private static async Task<string> AnswerAsync(Actor actor, Kind kind, string prefix, JobsTestSite site)
    {
        var own = $"{prefix}/test.space/runs";
        var (method, path, body) = kind switch
        {
            Kind.List or Kind.Services => (HttpMethod.Get, prefix, null),
            Kind.History => (HttpMethod.Get, own, null),
            Kind.Detail => (HttpMethod.Get, $"{own}/{site.OwnFailed}", null),
            Kind.Foreign => (HttpMethod.Get, $"{own}/{site.ForeignFailed}", null),
            Kind.Run => (HttpMethod.Post, own, null),
            Kind.Retry => (HttpMethod.Post, $"{own}/{site.OwnFailed}/retry", null),
            Kind.Cancel => (HttpMethod.Post, $"{own}/{site.OwnQueued}/cancel", null),
            _ => (HttpMethod.Put, $"{prefix}/test.space/paused", (object?)new { paused = true })
        };
        // A token sends no CSRF; the cookie sessions fetch theirs.
        var response = actor.Bearer
            ? await actor.Client.SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) })
            : await AtlasHttpSite.SendAsync(actor.Client, method, path, body);
        if (!response.IsSuccessStatusCode)
        {
            return ((int)response.StatusCode).ToString();
        }
        if (kind != Kind.Services)
        {
            return "ok";
        }
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["systemServices"]!.AsArray().Count > 0 ? "has" : "empty";
    }
}
