using NHibernate;
using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.KnowledgeMaintenance;

public sealed class GetAiUsageReportHandler(ISession session, ModuleSettingsAccess access, TimeProvider clock)
{
    public async Task<AiUsageReport> Handle(GetAiUsageReport query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var until = clock.GetUtcNow().UtcDateTime;
        var since = until.Date.AddDays(-6);
        var rows = await session.CreateSQLQuery(MeasuredUsageSql + """
            SELECT task,kind,operation,count(*) AS calls,
              count(*) FILTER (WHERE outcome NOT IN ('ok','canceled')) AS errors,
              sum(input_tokens) AS input_tokens,sum(output_tokens) AS output_tokens,
              count(input_tokens) AS input_known,count(output_tokens) AS output_known,
              percentile_cont(0.5) WITHIN GROUP (ORDER BY latency_ms) AS p50,
              percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms) AS p95,
              sum(known_cost) AS cost,count(known_cost) AS cost_known
            FROM measured
            GROUP BY task,kind,operation
            ORDER BY operation,calls DESC,task,kind LIMIT 200
            """)
            .AddScalar("task", NHibernateUtil.String).AddScalar("kind", NHibernateUtil.String).AddScalar("operation", NHibernateUtil.String)
            .AddScalar("calls", NHibernateUtil.Int64).AddScalar("errors", NHibernateUtil.Int64)
            .AddScalar("input_tokens", NHibernateUtil.Decimal).AddScalar("output_tokens", NHibernateUtil.Decimal)
            .AddScalar("input_known", NHibernateUtil.Int64).AddScalar("output_known", NHibernateUtil.Int64)
            .AddScalar("p50", NHibernateUtil.Double).AddScalar("p95", NHibernateUtil.Double).AddScalar("cost", NHibernateUtil.Decimal)
            .AddScalar("cost_known", NHibernateUtil.Int64)
            .SetParameter("since", since, NHibernateUtil.UtcDateTime).SetParameter("until", until, NHibernateUtil.UtcDateTime)
            .ListAsync<object[]>(ct);
        var report = new AiUsageReport(since, until, rows.Select(row => new AiUsageRow(
            (string)row[0], (string)row[1], (string)row[2], (long)row[3], (long)row[4],
            row[5] is decimal input ? (long?)input : null, row[6] is decimal output ? (long?)output : null,
            (long)row[7], (long)row[8], (double)row[9], (double)row[10], row[11] as decimal?)
            { CostKnownCalls = (long)row[12] }).ToArray());
        return report with { Providers = await ProvidersAsync(since, until, ct), Days = await DaysAsync(since, until, ct) };
    }

    private const string MeasuredUsageSql = """
        WITH measured AS (
          SELECT u."OccurredAt" AS occurred_at,u."Model" AS model,u."Outcome" AS outcome,
            u."Task" AS task,u."Kind" AS kind,u."Operation" AS operation,u."LatencyMs" AS latency_ms,
            u."InputTokens" AS input_tokens,u."OutputTokens" AS output_tokens,
            r."Provider" AS provider,r."Location" AS location,
            CASE WHEN (u."Operation"='embedding' AND u."InputTokens" IS NOT NULL AND r."InputPricePerMillion" IS NOT NULL)
              OR (u."Operation"<>'embedding' AND u."InputTokens" IS NOT NULL AND u."OutputTokens" IS NOT NULL
                AND r."InputPricePerMillion" IS NOT NULL AND r."OutputPricePerMillion" IS NOT NULL)
            THEN (u."InputTokens"*r."InputPricePerMillion" +
              COALESCE(u."OutputTokens"*r."OutputPricePerMillion",0))/1000000 END AS known_cost
          FROM "AiUsage" u JOIN "AiProfileRevision" r ON r."Id"=u."ProfileRevisionId"
          WHERE u."OccurredAt">=:since AND u."OccurredAt"<=:until
        )
        """;

    private async Task<IReadOnlyList<AiUsageProviderRow>> ProvidersAsync(DateTime since, DateTime until, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery(MeasuredUsageSql + """
            SELECT provider,location,model,count(*) AS calls,
              count(*) FILTER (WHERE outcome NOT IN ('ok','canceled')) AS errors,
              count(*) FILTER (WHERE outcome='canceled') AS canceled,
              sum(input_tokens) AS input_tokens,sum(output_tokens) AS output_tokens,
              count(input_tokens) AS input_known,count(output_tokens) AS output_known,
              count(known_cost) AS cost_known,sum(known_cost) AS cost,max(occurred_at) AS last_call
            FROM measured GROUP BY provider,location,model
            ORDER BY calls DESC,provider,location,model
            """)
            .AddScalar("provider", NHibernateUtil.String).AddScalar("location", NHibernateUtil.String)
            .AddScalar("model", NHibernateUtil.String).AddScalar("calls", NHibernateUtil.Int64)
            .AddScalar("errors", NHibernateUtil.Int64).AddScalar("canceled", NHibernateUtil.Int64)
            .AddScalar("input_tokens", NHibernateUtil.Decimal).AddScalar("output_tokens", NHibernateUtil.Decimal)
            .AddScalar("input_known", NHibernateUtil.Int64).AddScalar("output_known", NHibernateUtil.Int64)
            .AddScalar("cost_known", NHibernateUtil.Int64).AddScalar("cost", NHibernateUtil.Decimal)
            .AddScalar("last_call", NHibernateUtil.UtcDateTime)
            .SetParameter("since", since, NHibernateUtil.UtcDateTime).SetParameter("until", until, NHibernateUtil.UtcDateTime)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new AiUsageProviderRow(
            (string)row[0], (string)row[1], (string)row[2], (long)row[3], (long)row[4], (long)row[5],
            row[6] is decimal input ? (long?)input : null, row[7] is decimal output ? (long?)output : null,
            (long)row[8], (long)row[9], (long)row[10], row[11] as decimal?, (DateTime)row[12])).ToArray();
    }

    private async Task<IReadOnlyList<AiUsageDayRow>> DaysAsync(DateTime firstDay, DateTime until, CancellationToken ct)
    {

        var rows = await session.CreateSQLQuery(MeasuredUsageSql + """
            SELECT date_trunc('day',timezone('UTC',occurred_at)) AS day,count(*) AS calls,
              count(*) FILTER (WHERE outcome NOT IN ('ok','canceled')) AS errors,
              count(*) FILTER (WHERE outcome='canceled') AS canceled,
              sum(input_tokens) AS input_tokens,sum(output_tokens) AS output_tokens,
              count(input_tokens) AS input_known,count(output_tokens) AS output_known,
              count(known_cost) AS cost_known,sum(known_cost) AS cost
            FROM measured GROUP BY day ORDER BY day
            """)
            .AddScalar("day", NHibernateUtil.DateTime).AddScalar("calls", NHibernateUtil.Int64)
            .AddScalar("errors", NHibernateUtil.Int64).AddScalar("canceled", NHibernateUtil.Int64)
            .AddScalar("input_tokens", NHibernateUtil.Decimal).AddScalar("output_tokens", NHibernateUtil.Decimal)
            .AddScalar("input_known", NHibernateUtil.Int64).AddScalar("output_known", NHibernateUtil.Int64)
            .AddScalar("cost_known", NHibernateUtil.Int64).AddScalar("cost", NHibernateUtil.Decimal)
            .SetParameter("since", firstDay, NHibernateUtil.UtcDateTime).SetParameter("until", until, NHibernateUtil.UtcDateTime)
            .ListAsync<object[]>(ct);
        var byDay = rows.Select(row => new AiUsageDayRow(
            (DateTime)row[0], (long)row[1], (long)row[2], (long)row[3],
            row[4] is decimal input ? (long?)input : null, row[5] is decimal output ? (long?)output : null,
            (long)row[6], (long)row[7], (long)row[8], row[9] as decimal?))
            .ToDictionary(row => row.Day.Date);
        return Enumerable.Range(0, 7).Select(offset =>
        {
            var day = firstDay.AddDays(offset);
            return byDay.GetValueOrDefault(day) ?? new AiUsageDayRow(day, 0, 0, 0, null, null, 0, 0, 0, null);
        }).ToArray();
    }
}

public sealed class GetAiQualityReportHandler(ISession session, ModuleSettingsAccess access, TimeProvider clock)
{
    public async Task<AiQualityReport> Handle(GetAiQualityReport query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var until = clock.GetUtcNow().UtcDateTime;
        var since = until.AddDays(-7);
        // Intentionally never joins messages or selects Note: even a deleted conversation's anonymous rating remains useful.
        var rows = await session.CreateSQLQuery("""
            SELECT "Task" AS task,"PromptVersion" AS prompt,"Rating" AS rating,"Reason" AS reason,count(*) AS total
            FROM "AiFeedback" WHERE "CreatedAt">=:since AND "CreatedAt"<=:until
            GROUP BY "Task","PromptVersion","Rating","Reason"
            ORDER BY "Task","PromptVersion","Rating","Reason" LIMIT 200
            """).AddScalar("task", NHibernateUtil.String).AddScalar("prompt", NHibernateUtil.String)
            .AddScalar("rating", NHibernateUtil.String).AddScalar("reason", NHibernateUtil.String).AddScalar("total", NHibernateUtil.Int64)
            .SetParameter("since", since, NHibernateUtil.UtcDateTime).SetParameter("until", until, NHibernateUtil.UtcDateTime)
            .ListAsync<object[]>(ct);
        // Aggregate retained conversations only; never select question, answer, owner or conversation identifiers.
        var noSources = await session.CreateSQLQuery("""
            SELECT s."Id" AS space_id,COALESCE(s."Name",'Celá wiki nebo odstraněný prostor') AS space_name,count(*) AS total
            FROM "AiMessage" a
            JOIN "AiMessage" q ON q."ConversationId"=a."ConversationId" AND q."Ordinal"=a."Ordinal"-1 AND q."Role"='user'
            LEFT JOIN "Page" p ON p."Id"=q."ScopePageId"
            LEFT JOIN "Space" s ON s."Id"=COALESCE(q."ScopeSpaceId",p."SpaceId")
            WHERE a."Role"='assistant' AND a."State"='no_sources' AND a."CreatedAt">=:since AND a."CreatedAt"<=:until
            GROUP BY s."Id",s."Name" ORDER BY total DESC,s."Name" LIMIT 200
            """).AddScalar("space_id", NHibernateUtil.Guid).AddScalar("space_name", NHibernateUtil.String).AddScalar("total", NHibernateUtil.Int64)
            .SetParameter("since", since, NHibernateUtil.UtcDateTime).SetParameter("until", until, NHibernateUtil.UtcDateTime)
            .ListAsync<object[]>(ct);
        var interactions = await session.CreateSQLQuery("""
            SELECT "Task","Kind","Outcome",sum("Count") AS total FROM "AiInteractionDaily"
            WHERE "Day">=:since AND "Day"<=:until GROUP BY "Task","Kind","Outcome" ORDER BY "Task","Kind","Outcome"
            """).AddScalar("Task", NHibernateUtil.String).AddScalar("Kind", NHibernateUtil.String)
            .AddScalar("Outcome", NHibernateUtil.String).AddScalar("total", NHibernateUtil.Decimal)
            .SetParameter("since", until.Date.AddDays(-6), NHibernateUtil.Date).SetParameter("until", until.Date, NHibernateUtil.Date)
            .ListAsync<object[]>(ct);
        return new AiQualityReport(since, until, rows.Select(row => new AiQualityRow(
            (string)row[0], row[1] as string, (string)row[2], row[3] as string, (long)row[4])).ToArray(), interactions.Select(row => new AiInteractionRow((string)row[0], (string)row[1], (string)row[2], (long)(decimal)row[3])).ToArray(),
            noSources.Select(row => new AiNoSourcesRow(row[0] as Guid?, (string)row[1], (long)row[2])).ToArray());
    }
}
