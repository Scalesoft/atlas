using Atlas.Server.Features.Administration;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Logs;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AppLogs;

/// <param name="Level">The least level: trace, debug, information, warning, error, critical.</param>
/// <param name="Category">A prefix of the category (the logger's type name).</param>
/// <param name="Q">Text in the message or the exception, any case.</param>
/// <param name="TraceId">The request's trace id.</param>
/// <param name="Exception">Only lines with an exception.</param>
/// <param name="JobRunId">The lines of one background job run.</param>
public sealed record AppLogFilter(string? Level = null, string? Category = null, string? Q = null, string? TraceId = null, bool? Exception = null,
    DateTime? From = null, DateTime? To = null, long? JobRunId = null);

/// <summary>A page of the server's log, newest first: older than <paramref name="Before"/>, or with <paramref name="After"/> the newest lines
/// above it (the live tail). With neither, the newest lines and the summary.</summary>
public sealed record ListAppLog(AppLogFilter Filter, long? Before = null, long? After = null, int Take = AppLogQueryHandler.DefaultTake);

/// <param name="Exception">The exception's whole text (inner ones and stack), redacted and capped.</param>
/// <param name="JobId">The job of <paramref name="JobRunId"/>, with <paramref name="JobTitle"/>; null once the run aged out.</param>
public sealed record AppLogEntryView(long Id, DateTime At, string Level, string Category, int? EventId, string Message, string? ExceptionType,
    string? ExceptionMessage, string? Exception, string? TraceId, Guid? UserId, long? JobRunId, string? JobId, string? JobTitle);

/// <param name="Dropped">Lines this server process dropped since its start because the queue was full.</param>
public sealed record AppLogSummary(int ErrorsHour, int WarningsHour, int ErrorsDay, int WarningsDay, long Dropped);

/// <param name="More">With <c>before</c> (or neither), older lines exist; with <c>after</c>, more new lines arrived than came: a gap, so the
/// page replaces what the reader holds.</param>
public sealed record AppLogPage(IReadOnlyList<AppLogEntryView> Entries, bool More, AppLogSummary? Summary);

/// <summary>The server's log (Správa wiki → Systémový log, /api/v1/logs, atlas_read_logs) for a wiki administrator, Hub ones included:
/// one statement a page and one for the summary, whatever the log holds. IServiceProvider: the queue exists in the server only, and the
/// titles of the jobs are their definitions'.</summary>
public sealed class AppLogQueryHandler(ISession session, AdministrationAccess administration, IServiceProvider services)
{
    public const int DefaultTake = 100, MaxTake = 1000;

    // Native SQL, category 5 of docs/architecture.md: keyset-paged filter over the AppLog table (no entity), plus FILTER counts in SummaryAsync.
    public async Task<AppLogPage> Handle(ListAppLog query, CancellationToken ct)
    {
        await administration.RequireWikiAdminAsync(ct);
        var take = Math.Clamp(query.Take, 1, MaxTake);
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value, NHibernate.Type.IType Type)>();
        Where(query.Filter, conditions, parameters);
        if (query.Before is { } before)
        {
            conditions.Add("\"Id\" < :before");
            parameters.Add(("before", before, NHibernateUtil.Int64));
        }
        if (query.After is { } after)
        {
            conditions.Add("\"Id\" > :after");
            parameters.Add(("after", after, NHibernateUtil.Int64));
        }
        var sql = session.CreateSQLQuery($"""
            SELECT "Id" AS id, "At" AS at, "Level" AS level, "Category" AS category, "EventId" AS event, "Message" AS message, "ExceptionType" AS type,
              "ExceptionMessage" AS error, "Exception" AS exception, "TraceId" AS trace, "UserId" AS account, "JobRunId" AS run,
            (SELECT r."JobId" FROM "JobRun" r WHERE r."Id" = l."JobRunId") AS job
            FROM "AppLog" l {(conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions))} ORDER BY "Id" DESC LIMIT :take
            """);
        foreach (var (name, value, type) in parameters)
        {
            sql.SetParameter(name, value, type);
        }
        var rows = await sql.SetParameter("take", take + 1).ListAsync<object[]>(ct);
        var titles = services.GetServices<IJobDefinition>().ToDictionary(definition => definition.Id, definition => definition.Title, StringComparer.Ordinal);
        var entries = rows.Take(take).Select(row => new AppLogEntryView(Convert.ToInt64(row[0]), DateTime.SpecifyKind((DateTime)row[1], DateTimeKind.Utc),
            ((LogLevel)Convert.ToInt32(row[2])).ToString().ToLowerInvariant(), (string)row[3], row[4] is null ? null : Convert.ToInt32(row[4]), (string)row[5],
            (string?)row[6], (string?)row[7], (string?)row[8], (string?)row[9], (Guid?)row[10], row[11] is null ? null : Convert.ToInt64(row[11]), (string?)row[12],
            row[12] is string job ? titles.GetValueOrDefault(job) ?? job : null)).ToList();
        return new AppLogPage(entries, rows.Count > take, query.Before is null && query.After is null ? await SummaryAsync(ct) : null);
    }

    private async Task<AppLogSummary> SummaryAsync(CancellationToken ct)
    {
        var row = (await session.CreateSQLQuery("""
            SELECT count(*) FILTER (WHERE "Level" >= 4 AND "At" >= now() - interval '1 hour') AS errors_hour,
              count(*) FILTER (WHERE "Level" = 3 AND "At" >= now() - interval '1 hour') AS warnings_hour,
              count(*) FILTER (WHERE "Level" >= 4) AS errors_day, count(*) FILTER (WHERE "Level" = 3) AS warnings_day
            FROM "AppLog" WHERE "Level" >= 3 AND "At" >= now() - interval '24 hours'
            """).ListAsync<object[]>(ct)).Single();
        return new AppLogSummary(Convert.ToInt32(row[0]), Convert.ToInt32(row[1]), Convert.ToInt32(row[2]), Convert.ToInt32(row[3]),
            services.GetService<AppLogQueue>()?.Dropped ?? 0);
    }

    private static void Where(AppLogFilter filter, List<string> conditions, List<(string, object?, NHibernate.Type.IType)> parameters)
    {
        if (!string.IsNullOrWhiteSpace(filter.Level))
        {
            if (!Enum.TryParse<LogLevel>(filter.Level, true, out var level) || level == LogLevel.None || int.TryParse(filter.Level, out _))
            {
                throw new ApiException(400, "Neznámá úroveň logu; použijte trace, debug, information, warning, error nebo critical.", "invalid_level");
            }
            conditions.Add("\"Level\" >= :level");
            parameters.Add(("level", (short)level, NHibernateUtil.Int16));
        }
        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            conditions.Add("\"Category\" LIKE :category");
            parameters.Add(("category", Like(filter.Category.Trim()) + "%", NHibernateUtil.String));
        }
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            conditions.Add("(\"Message\" ILIKE :q OR \"Exception\" ILIKE :q)");
            parameters.Add(("q", "%" + Like(filter.Q.Trim()) + "%", NHibernateUtil.String));
        }
        if (!string.IsNullOrWhiteSpace(filter.TraceId))
        {
            conditions.Add("\"TraceId\" = :trace");
            parameters.Add(("trace", filter.TraceId.Trim(), NHibernateUtil.String));
        }
        if (filter.JobRunId is { } run)
        {
            conditions.Add("\"JobRunId\" = :run");
            parameters.Add(("run", run, NHibernateUtil.Int64));
        }
        if (filter.Exception == true)
        {
            conditions.Add("\"ExceptionType\" IS NOT NULL");
        }
        if (filter.From is { } from)
        {
            conditions.Add("\"At\" >= :from");
            parameters.Add(("from", from.ToUniversalTime(), NHibernateUtil.UtcDateTime));
        }
        if (filter.To is { } to)
        {
            conditions.Add("\"At\" < :to");
            parameters.Add(("to", to.ToUniversalTime(), NHibernateUtil.UtcDateTime));
        }
    }

    /// <summary>The text of a LIKE pattern, its wildcards literal (PostgreSQL's default escape is the backslash).</summary>
    private static string Like(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
