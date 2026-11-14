using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;

namespace Atlas.Server.Features.AppLogs;

/// <summary>The server's log over MCP (3.6, AI parity with Správa wiki → Systémový log): what a wiki administrator reads there, as the token
/// owner, through the same handler and its check. Bounded for a model: at most <see cref="MaxTake"/> lines, each shortened, the stack of the
/// first <see cref="Stacks"/> exceptions only. Not a Wolverine handler: the class name does not end with "Handler".</summary>
[McpServerToolType]
public sealed class AppLogMcpTools(IMessageBus bus)
{
    public const int MaxTake = 50;

    private const int Stacks = 3;

    [McpServerTool(Name = "atlas_read_logs", Title = "Systémový log", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Přečte log serveru Atlasu (Správa wiki → Systémový log), jen pro správce wiki: řádky od nejnovějšího s časem, úrovní, kategorií, zprávou, " +
        "výjimkou a traceId požadavku; hesla a tokeny jsou skryté. Filtry: nejnižší úroveň (warning, error…), začátek kategorie, text, traceId, jen s výjimkou, " +
        "čas od a do, běh úlohy (jobRunId = runId). Starší řádky: before = id posledního řádku. Nahoře souhrn chyb a varování za hodinu a 24 h. " + McpText.TrustNotice)]
    public async Task<CallToolResult> ReadAsync(
        [Description("Nejnižší úroveň: trace, debug, information, warning, error, critical.")] string? level = null,
        [Description("Začátek kategorie (jméno třídy loggeru), např. Atlas.Server.Features.Backups.")] string? category = null,
        [Description("Text ve zprávě nebo výjimce.")] string? text = null,
        [Description("traceId požadavku: všechny jeho řádky.")] string? traceId = null,
        [Description("Jen řádky s výjimkou.")] bool exceptionsOnly = false,
        [Description("Od (ISO 8601).")] DateTime? from = null,
        [Description("Do (ISO 8601).")] DateTime? to = null,
        [Description("runId běhu úlohy na pozadí (atlas_list_jobs, atlas_get_job_run): jen řádky tohoto běhu.")] long? jobRunId = null,
        [Description("Jen řádky starší než toto id (stránkování).")] long? before = null,
        [Description("Kolik řádků, nejvýš 50.")] int take = 20,
        CancellationToken ct = default)
    {
        var page = await bus.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter(level, category, text, traceId, exceptionsOnly ? true : null, from, to, jobRunId),
            before, Take: Math.Clamp(take, 1, MaxTake)), ct);
        var body = new StringBuilder();
        var stacks = 0;
        var entries = new JsonArray();
        foreach (var entry in page.Entries)
        {
            body.Append("- ").Append(entry.Id).Append(' ').Append(Time(entry.At)).Append(' ').Append(entry.Level.ToUpperInvariant()).Append(' ')
                .Append(McpText.Line(entry.Category, 120)).Append(": ").Append(McpText.Line(entry.Message, 300))
                .Append(entry.TraceId is { } trace ? " (traceId: " + trace + ")" : "")
                .Append(entry.JobRunId is { } run ? $" (úloha {McpText.Line(entry.JobTitle ?? entry.JobId ?? "?", 80)}, jobId: {entry.JobId}, runId: {run})" : "").Append('\n');
            if (entry.ExceptionType is { } type)
            {
                body.Append("  ").Append(McpText.Line(type, 150)).Append(": ").Append(McpText.Line(entry.ExceptionMessage, 300)).Append('\n');
                if (stacks++ < Stacks && entry.Exception is { } stack)
                {
                    body.Append(McpText.Content(stack.Length > 1200 ? stack[..McpText.CutAt(stack, 1200)] + "…" : stack)).Append('\n');
                }
            }
            entries.Add(new JsonObject
            {
                ["id"] = entry.Id, ["at"] = entry.At, ["level"] = entry.Level, ["category"] = entry.Category, ["message"] = McpText.Line(entry.Message, 300),
                ["exceptionType"] = entry.ExceptionType, ["exceptionMessage"] = entry.ExceptionMessage is null ? null : McpText.Line(entry.ExceptionMessage, 300),
                ["traceId"] = entry.TraceId, ["userId"] = entry.UserId?.ToString(), ["jobRunId"] = entry.JobRunId, ["jobId"] = entry.JobId
            });
        }
        var summary = page.Summary;
        var heading = summary is null ? $"Log serveru: {page.Entries.Count} řádků."
            : $"Log serveru: {page.Entries.Count} řádků. Za hodinu {summary.ErrorsHour} chyb a {summary.WarningsHour} varování, za 24 h {summary.ErrorsDay} chyb a " +
              $"{summary.WarningsDay} varování" + (summary.Dropped > 0 ? $"; {summary.Dropped} řádků se nevešlo do fronty a chybí." : ".");
        var structured = new JsonObject { ["entries"] = entries, ["more"] = page.More };
        if (summary is not null)
        {
            structured["summary"] = JsonSerializer.SerializeToNode(summary, JsonSerializerOptions.Web);
        }
        return McpText.Result(McpText.Wrap(heading, page.Entries.Count == 0 ? "Žádné řádky." : body.ToString(),
            page.More && page.Entries.Count > 0 ? $"Starší řádky: before = {page.Entries[^1].Id}." : null), structured);
    }

    private static string Time(DateTime at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture);
}
