using System.Text.Json;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.AppLogs;

/// <summary>The query of the log endpoints: the filter, a cursor and the page size.</summary>
public sealed record AppLogRequest(string? Level, string? Category, string? Q, string? TraceId, bool? Exception, DateTime? From, DateTime? To, long? Before,
    long? After, int? Take, long? JobRunId)
{
    public AppLogFilter Filter => new(Level, Category, Q, TraceId, Exception, From, To, JobRunId);
}

/// <summary>The server's log (3.6): the cookie API of Správa wiki → Systémový log and, for AI parity, the same reads for personal tokens under
/// /api/v1 with the read scope. A wiki administrator only (the handler's check). The live tail polls with <c>after</c>, as the jobs page polls
/// its runs: one statement a poll, at most a page of lines, whichever replica wrote them.</summary>
public static class AppLogEndpoints
{
    public static void MapAppLog(this IEndpointRouteBuilder app)
    {
        Map(app.MapGroup("/api/admin").RequireAuthorization(), false);
        Map(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void Map(RouteGroupBuilder group, bool personalApi)
    {
        var list = group.MapGet("/logs", async ([AsParameters] AppLogRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AppLogPage>(new ListAppLog(request.Filter, request.Before, request.After, request.Take ?? AppLogQueryHandler.DefaultTake), ct)))
            .Produces<AppLogPage>().ProducesProblem(400).ProducesProblem(403)
            .WithSummary("The server's own log, newest first, for a wiki administrator: filter by the least level, a category prefix, text, trace id, " +
                "exceptions only, time and a background job run (jobRunId); page older with before (the last id), follow new lines with after (the newest id). Secrets are redacted.");
        var export = group.MapGet("/logs/export", async ([AsParameters] AppLogRequest request, IMessageBus bus, HttpContext http) =>
            {
                // The first page before the response starts: a refusal is still a status, not a broken download.
                var page = await bus.InvokeAsync<AppLogPage>(new ListAppLog(request.Filter, request.Before, Take: AppLogQueryHandler.MaxTake), http.RequestAborted);
                return Results.Stream(async stream =>
                {
                    while (true)
                    {
                        foreach (var entry in page.Entries)
                        {
                            await JsonSerializer.SerializeAsync(stream, entry, JsonSerializerOptions.Web, http.RequestAborted);
                            await stream.WriteAsync("\n"u8.ToArray(), http.RequestAborted);
                        }
                        await stream.FlushAsync(http.RequestAborted);
                        if (!page.More || page.Entries.Count == 0)
                        {
                            break;
                        }
                        page = await bus.InvokeAsync<AppLogPage>(new ListAppLog(request.Filter, page.Entries[^1].Id, Take: AppLogQueryHandler.MaxTake), http.RequestAborted);
                    }
                }, "application/x-ndjson", $"atlas-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.ndjson");
            })
            .Produces(200, contentType: "application/x-ndjson").ProducesProblem(400).ProducesProblem(403)
            .WithSummary("The filtered log as NDJSON, one line per entry, newest first, streamed page by page.");
        if (personalApi)
        {
            list.WithName("ListAppLog");
            export.WithName("ExportAppLog");
        }
    }
}
