using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Jobs.Commands;
using Atlas.Server.Features.Jobs.Queries;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;

namespace Atlas.Server.Features.Jobs;

/// <summary>
/// Background jobs over MCP (3.3 plan §4.7): what a wiki administrator (and for a job of their space its administrator) does in Správa wiki →
/// Úlohy na pozadí, as the token owner: find out why a backup did not run and run it again. Pausing is an operational setting and stays the
/// web's and the API's. Errors are the handlers' ApiExceptions, which the call filter turns into tool errors and audits. Not a Wolverine
/// handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class JobMcpTools(IMessageBus bus)
{
    private const int LogLines = 200;

    [McpServerTool(Name = "atlas_list_jobs", Title = "Úlohy na pozadí", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Vypíše úlohy na pozadí (zálohy, údržba, fronty, propojení kontaktů…): jobId, název, kdy běží, stav a čas příštího běhu, pozastavení, poslední běh " +
        "(runId, stav, kdy) a poslední úspěch, zda je pravidelná, po termínu nebo zaseknutá. Nahoře souhrn: kolik běží, čeká a selhalo za 24 h a 7 dní, a nejnovější " +
        "selhané běhy s první řádkou chyby (structuredContent.summary, i běhy po dnech za 14 dní). Správce wiki vidí všechny a k tomu systémové služby (běží, chyba, neodpovídá); správce prostoru jen úlohy svých prostorů. Proč úloha " +
        "selhala řekne atlas_get_job_run, spustí ji atlas_run_job. " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListAsync(CancellationToken ct = default)
    {
        var list = await bus.InvokeAsync<JobList>(new ListJobs(), ct);
        var body = new StringBuilder();
        foreach (var job in list.Jobs)
        {
            body.Append("- ").Append(McpText.Line(job.Title, 120)).Append(" (jobId: ").Append(job.Id).Append(", ").Append(McpText.Line(job.Schedule, 80)).Append(", ")
                .Append(job.Paused ? "pozastavená" : job.LastRun?.State ?? "ještě neběžela");
            if (job.LastRun is { } last)
            {
                body.Append(", poslední běh runId: ").Append(last.Id).Append(' ').Append(Time(last.FinishedAt ?? last.StartedAt ?? last.QueuedAt));
            }
            if (job.NextDueAt is { } next)
            {
                body.Append(", příště ").Append(Time(next));
            }
            body.Append(job.Recurring ? ", pravidelná" : ", jednorázová nebo na vyžádání")
                .Append(job.LastSuccessAt is { } success ? ", naposledy úspěšně " + Time(success) : "")
                .Append(job.Overdue ? ", PO TERMÍNU" : "").Append(job.Stuck ? ", ZASEKNUTÁ (běží mnohem déle než obvykle)" : "")
                .Append(")\n");
        }
        var summary = list.Summary;
        foreach (var failure in summary.Failures)
        {
            body.Append("- Selhal běh runId: ").Append(failure.RunId).Append(" úlohy ").Append(McpText.Line(failure.JobTitle, 120)).Append(" (jobId: ").Append(failure.JobId)
                .Append(", ").Append(Time(failure.FinishedAt)).Append(failure.Error is { } error ? ", " + McpText.Line(error, 200) : "").Append(")\n");
        }
        foreach (var service in list.SystemServices)
        {
            body.Append("- Služba ").Append(McpText.Line(service.Title, 120)).Append(" (").Append(service.State).Append(service.Detail is { } detail ? ", " + McpText.Line(detail, 120) : "")
                .Append(service.Error is { } error ? ", chyba: " + McpText.Line(error, 200) : "").Append(")\n");
        }
        return McpText.Result(McpText.Wrap($"Úlohy na pozadí: {list.Jobs.Count}, systémové služby: {list.SystemServices.Count}. Běží {summary.Running}, ve frontě " +
                $"{summary.Queued}, selhalo za 24 h {summary.FailedDay} a za 7 dní {summary.FailedWeek} běhů, po termínu {list.Jobs.Count(job => job.Overdue)}, " +
                $"zaseknuté {list.Jobs.Count(job => job.Stuck)}.", list.Jobs.Count == 0 ? "Žádné úlohy." : body.ToString()),
            Structured(list));
    }

    [McpServerTool(Name = "atlas_get_job_run", Title = "Běh úlohy", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Bez runId vypíše historii úlohy (běhy od nejnovějšího, 20 na stránku); s runId ukáže jeden běh: stav, pokus, souhrn čísel, chybu a log. " +
        "jobId a runId z atlas_list_jobs. " + McpText.TrustNotice)]
    public async Task<CallToolResult> GetAsync(
        [Description("jobId z atlas_list_jobs.")] string jobId,
        [Description("Volitelně runId; bez něj historie úlohy.")] long? runId = null,
        [Description("Stránka historie od 1.")] int page = 1,
        CancellationToken ct = default)
    {
        if (runId is not { } id)
        {
            var runs = await bus.InvokeAsync<JobRunList>(new ListJobRuns(jobId, page), ct);
            var lines = new StringBuilder();
            foreach (var run in runs.Runs)
            {
                lines.Append("- runId: ").Append(run.Id).Append(", ").Append(run.State).Append(", ").Append(run.Trigger).Append(", ").Append(Time(run.QueuedAt))
                    .Append(run.Reason is { } reason ? ", " + McpText.Line(reason, 120) : "").Append('\n');
            }
            var more = runs.Total > runs.Page * runs.PageSize;
            return McpText.Result(McpText.Wrap($"Historie úlohy {McpText.Line(runs.Job.Title, 120)}: {runs.Runs.Count} z {runs.Total}, stránka {runs.Page}.",
                    runs.Runs.Count == 0 ? "Úloha ještě neběžela." : lines.ToString(), more ? $"Další běhy: page {runs.Page + 1}." : null),
                Structured(runs));
        }
        var detail = await bus.InvokeAsync<JobRunDetail>(new GetJobRun(jobId, id), ct);
        var text = new StringBuilder();
        var view = detail.Run;
        text.Append("Stav: ").Append(view.State).Append(", pokus ").Append(view.Attempt).Append(", spouštěč ").Append(view.Trigger).Append('\n');
        if (view.Reason is { } why)
        {
            text.Append("Důvod: ").Append(McpText.Line(why, 200)).Append('\n');
        }
        text.Append("Zařazeno ").Append(Time(view.QueuedAt)).Append(view.StartedAt is { } started ? ", spuštěno " + Time(started) : "")
            .Append(view.FinishedAt is { } finished ? ", dokončeno " + Time(finished) : "").Append('\n');
        if (view.Summary is { } summary)
        {
            text.Append("Souhrn: ").Append(McpText.Line(summary.ToJsonString(), 500)).Append('\n');
        }
        if (detail.Error is { } error)
        {
            text.Append("Chyba: ").Append(McpText.Content(error.Length > 2000 ? error[..McpText.CutAt(error, 2000)] + "…" : error)).Append('\n');
        }
        foreach (var entry in detail.Log.TakeLast(LogLines))
        {
            text.Append(Time(entry.At)).Append(' ').Append(entry.Level).Append(' ').Append(McpText.Line(entry.Message, 300)).Append('\n');
        }
        return McpText.Result(McpText.Wrap($"Běh {id} úlohy {McpText.Line(jobId, 80)}.", text.ToString(),
                detail.Log.Count > LogLines ? $"Log má {detail.Log.Count} záznamů, ukázal se posledních {LogLines}; celý je v GET /api/v1/jobs/{{jobId}}/runs/{{runId}}." : null),
            Structured(detail));
    }

    [McpServerTool(Name = "atlas_run_job", Title = "Spustit úlohu", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [Description("Spustí úlohu na pozadí teď (jako „Spustit teď“), i pozastavenou; sloučí se s během, který už čeká. S retryRunId zopakuje selhaný nebo zastavený " +
        "běh od jeho checkpointu („Zkusit znovu“). Spustit smí jen správce wiki; zopakovat běh i správce prostoru, jehož běh to je. Běh startuje hned, " +
        "jeho stav ukáže atlas_get_job_run.")]
    public async Task<CallToolResult> RunAsync(
        [Description("jobId z atlas_list_jobs.")] string jobId,
        [Description("Volitelně runId selhaného nebo zastaveného běhu, který se má zopakovat.")] long? retryRunId = null,
        CancellationToken ct = default)
    {
        var started = retryRunId is { } retry
            ? await bus.InvokeAsync<JobRunRef>(new RetryJobRun(jobId, retry), ct)
            : await bus.InvokeAsync<JobRunRef>(new RunJobNow(jobId), ct);
        return McpText.Result($"Úloha spuštěna.\n- jobId: {jobId}\n- runId: {started.RunId}\n", new JsonObject { ["jobId"] = jobId, ["runId"] = started.RunId });
    }

    [McpServerTool(Name = "atlas_cancel_job_run", Title = "Zastavit běh úlohy", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [Description("Zastaví čekající nebo běžící běh úlohy na pozadí: čekající skončí hned, běžící u dalšího kroku; co už potvrdil, zůstane. Hotový běh nejde zastavit " +
        "(409 run_finished). jobId a runId z atlas_list_jobs a atlas_get_job_run.")]
    public async Task<CallToolResult> CancelAsync(
        [Description("jobId z atlas_list_jobs.")] string jobId,
        [Description("runId běhu, který se má zastavit.")] long runId,
        CancellationToken ct = default)
    {
        await bus.InvokeAsync<JobRunRef>(new CancelJobRun(jobId, runId), ct);
        return McpText.Result($"Běh zastaven.\n- jobId: {jobId}\n- runId: {runId}\n", new JsonObject { ["jobId"] = jobId, ["runId"] = runId });
    }

    private static string Time(DateTime at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static JsonObject Structured<T>(T value) => JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web)!.AsObject();
}
