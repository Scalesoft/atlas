using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Jobs.Commands;
using Atlas.Server.Features.Jobs.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Platform.Localization;
using Wolverine;

namespace Atlas.Server.Features.Jobs;

public sealed record SetJobPausedRequest(bool Paused);

/// <summary>Background jobs (3.3 plan §4.6, §4.7). The cookie API of Správa wiki and, for AI parity, the same routes for personal tokens
/// under /api/v1: reads with the read scope, every write with the write scope, audited. The rights are the handlers': a wiki
/// administrator, and for a job of their space its administrator, as the token's owner. Pausing is an operational setting, so it has no MCP tool.</summary>
public static class JobEndpoints
{
    public static void MapJobs(this IEndpointRouteBuilder app)
    {
        Map(app.MapGroup("/api/admin").RequireAuthorization(), false);
        Map(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void Map(RouteGroupBuilder group, bool personalApi)
    {
        // The cookie API follows the request's language; the token API (AI parity) keeps the Czech definitions.
        string Language(HttpContext http) => personalApi ? RequestLanguage.Czech : RequestLanguage.Of(http);
        var list = group.MapGet("/jobs", async (HttpContext http, IMessageBus bus, CancellationToken ct)
                => Results.Ok(JobTexts.Translate(await bus.InvokeAsync<JobList>(new ListJobs(), ct), Language(http))))
            .Produces<JobList>().ProducesProblem(403)
            .WithSummary("The background jobs with their state, schedule, next run and newest run, and (for a wiki administrator) the system services. " +
                "A space administrator gets only the jobs with runs of their spaces.");
        var runs = group.MapGet("/jobs/{jobId}/runs", async (HttpContext http, string jobId, int? page, IMessageBus bus, CancellationToken ct)
                => Results.Ok(JobTexts.Translate(await bus.InvokeAsync<JobRunList>(new ListJobRuns(jobId, page ?? 1), ct), Language(http))))
            .Produces<JobRunList>().ProducesProblem(403).ProducesProblem(404)
            .WithSummary("The history of a job: its runs, newest first, 20 per page.");
        var run = group.MapGet("/jobs/{jobId}/runs/{runId:long}", async (string jobId, long runId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<JobRunDetail>(new GetJobRun(jobId, runId), ct)))
            .Produces<JobRunDetail>().ProducesProblem(403).ProducesProblem(404)
            .WithSummary("One run with its summary, error and log (up to 500 entries).");
        var runNow = group.MapPost("/jobs/{jobId}/runs", async (string jobId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<JobRunRef>(new RunJobNow(jobId), ct)))
            .Produces<JobRunRef>().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Run the job now, even a paused one; it merges into a run already queued. The run starts at once, follow it by its id.");
        var retry = group.MapPost("/jobs/{jobId}/runs/{runId:long}/retry", async (string jobId, long runId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<JobRunRef>(new RetryJobRun(jobId, runId), ct)))
            .Produces<JobRunRef>().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Run a failed or stopped run again, from where it stopped.");
        var cancel = group.MapPost("/jobs/{jobId}/runs/{runId:long}/cancel", async (string jobId, long runId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<JobRunRef>(new CancelJobRun(jobId, runId), ct)))
            .Produces<JobRunRef>().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Stop a queued or running run; what it already committed stays.");
        var pause = group.MapPut("/jobs/{jobId}/paused", async (string jobId, SetJobPausedRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new SetJobPaused(jobId, request.Paused), ct);
                return Results.NoContent();
            })
            .Produces(204).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Pause or resume every trigger of the job (a wiki administrator only). A request of an event meanwhile waits and runs on resume.");
        if (personalApi)
        {
            list.WithName("ListJobs");
            runs.WithName("ListJobRuns");
            run.WithName("GetJobRun");
            runNow.WithName("RunJob").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.JobRun);
            retry.WithName("RetryJobRun").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.JobRetry);
            cancel.WithName("CancelJobRun").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.JobCancel);
            pause.WithName("SetJobPaused").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.JobPause);
        }
    }
}
