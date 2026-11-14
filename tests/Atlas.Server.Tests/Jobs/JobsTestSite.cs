using System.Net;
using System.Net.Http.Headers;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;

namespace Atlas.Server.Tests.Jobs;

public sealed class TestPlatformJob : IJobDefinition
{
    public string Id => "test.platform";
    public string Title => "Zkušební úloha platformy";
    public string Description => "Zkušební úloha mimo prostor.";
    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromHours(1));
    public Task RunAsync(JobContext context, CancellationToken ct) => Task.CompletedTask;
}

public sealed class TestSpaceJob : IJobDefinition
{
    public string Id => "test.space";
    public string Title => "Zkušební úloha prostoru";
    public string Description => "Zkušební úloha, jejíž běhy patří prostorům.";
    public Task RunAsync(JobContext context, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>What the handlers asked of IJobControl (the job runtime's, core's): the rights are the UI stream's, so the rights tests stand the control in with this.</summary>
internal sealed class JobControlRecorder
{
    public List<string> Calls { get; } = [];
}

internal sealed class RecordingJobControl(JobControlRecorder recorder) : IJobControl
{
    public Task<long> RunNowAsync(string jobId, Guid? accountId, string reason, CancellationToken ct = default)
    {
        recorder.Calls.Add($"run {jobId} {reason}");
        return Task.FromResult(900L);
    }

    public Task<long> RetryAsync(long runId, Guid? accountId, CancellationToken ct = default)
    {
        recorder.Calls.Add($"retry {runId}");
        return Task.FromResult(901L);
    }

    public Task CancelAsync(long runId, Guid? accountId, CancellationToken ct = default)
    {
        recorder.Calls.Add($"cancel {runId}");
        return Task.CompletedTask;
    }

    public Task SetPausedAsync(string jobId, bool paused, Guid? accountId, CancellationToken ct = default)
    {
        recorder.Calls.Add($"pause {jobId} {paused}");
        return Task.CompletedTask;
    }
}

/// <summary>A site with two jobs, a platform one and one with runs in two spaces, seeded as rows (the job runtime is core's), the control recorded.</summary>
internal sealed class JobsTestSite : IAsyncDisposable
{
    private JobsTestSite(McpSite f, JobControlRecorder recorder) { F = f; Recorder = recorder; }

    public McpSite F { get; }
    public JobControlRecorder Recorder { get; }
    public string Own { get; private set; } = "";
    public string Foreign { get; private set; } = "";
    public long PlatformFailed { get; private set; }
    public long OwnFailed { get; private set; }
    public long OwnQueued { get; private set; }
    public long ForeignFailed { get; private set; }

    /// <param name="recorded">The control is a recorder (the rights are the test's); false leaves the job runtime's own control (a requested run stays queued in this host).</param>
    /// <param name="more">More services, e.g. a job of the test.</param>
    public static async Task<JobsTestSite> CreateAsync(bool recorded = true, Action<IServiceCollection>? more = null)
    {
        var recorder = new JobControlRecorder();
        var f = await McpSite.CreateAsync(services: services =>
        {
            services.AddJob<TestPlatformJob>();
            services.AddJob<TestSpaceJob>();
            services.AddSingleton(recorder);
            more?.Invoke(services);
            if (recorded)
            {
                services.AddScoped<IJobControl, RecordingJobControl>();
            }
        });
        var site = new JobsTestSite(f, recorder) { Own = await f.SpaceAsync("Vlastní prostor"), Foreign = await f.SpaceAsync("Cizí prostor") };
        // ON CONFLICT: the runtime of core creates the row of every registered definition at start.
        await f.Site.ExecuteAsync("""
            INSERT INTO "Job" ("Id","ModuleId") VALUES ('test.platform','platform'),('test.space','platform'),('test.projector','projector'),('test.gone','platform')
            ON CONFLICT DO NOTHING
            """);
        site.PlatformFailed = await site.RunAsync("test.platform", "failed", null, error: "System.InvalidOperationException: boom\n   at Atlas.Jobs.Test()");
        site.OwnFailed = await site.RunAsync("test.space", "failed", site.Own, error: "System.InvalidOperationException: nic\n   at Atlas.Jobs.Own()",
            log: """[{"at":"2026-11-01T10:00:00Z","level":"Information","message":"Zpracováno 3 stránek"},{"at":"2026-11-01T10:00:01Z","level":"Warning","message":"Stránka 7 se přeskočila"}]""",
            summary: """{"pages":3}""");
        site.OwnQueued = await site.RunAsync("test.space", "queued", site.Own);
        site.ForeignFailed = await site.RunAsync("test.space", "failed", site.Foreign, error: "cizí");
        await site.RunAsync("test.gone", "succeeded", null);
        await site.RunAsync("test.projector", "succeeded", null);
        return site;
    }

    public async Task<long> RunAsync(string jobId, string state, string? space, string? error = null, string? log = null, string? summary = null)
    {
        var finished = state is "queued" or "running" ? "NULL" : "now()";
        return (long)(await F.Site.ScalarAsync($"""
            INSERT INTO "JobRun" ("JobId","Trigger","State","Attempt","SpaceId","StartedAt","FinishedAt","Error","Log","Summary")
            VALUES (@job,'event',@state,1,@space,now(),{finished},@error,CAST(@log AS jsonb),CAST(@summary AS jsonb)) RETURNING "Id"
            """, ("job", jobId), ("state", state), ("space", space is null ? DBNull.Value : Guid.Parse(space)), ("error", (object?)error ?? DBNull.Value),
            ("log", (object?)log ?? DBNull.Value), ("summary", (object?)summary ?? DBNull.Value)))!;
    }

    /// <summary>An account with the role in the space (null: none), signed in; its token with the scopes on both channels, when given.</summary>
    public async Task<(HttpClient Cookie, HttpClient? Api, McpClient? Mcp, string Id)> UserAsync(string username, string? space, string? role,
        string[]? scopes = null)
    {
        var (client, id) = await F.UserAsync(username, scopes is null ? "read" : "write");
        if (role == "Admin")
        {
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(F.Admin, HttpMethod.Put, $"/api/spaces/{space}/access",
                new { userId = id, role, expectedVersion = (string?)null }, await AtlasHttpSite.ReauthAsync(F.Admin, F.Site.Password))).StatusCode);
        }
        else if (role is not null)
        {
            await F.GrantAsync(space!, id, role);
        }
        if (scopes is null)
        {
            return (client, null, null, id);
        }
        var (_, secret) = await McpSite.IssueAsync(client, F.Site.Password, scopes, ["rest", "mcp"], username);
        return (client, Bearer(secret), await F.ClientAsync(secret), id);
    }

    public HttpClient Bearer(string secret)
    {
        var client = F.Site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    public async Task<(HttpClient Api, McpClient Mcp)> AdminTokenAsync(params string[] scopes)
    {
        await F.SetLevelAsync(F.AdminId, "write");
        var (_, secret) = await McpSite.IssueAsync(F.Admin, F.Site.Password, scopes, ["rest", "mcp"], "Admin " + string.Join('+', scopes));
        return (Bearer(secret), await F.ClientAsync(secret));
    }

    public ValueTask DisposeAsync() => F.DisposeAsync();
}
