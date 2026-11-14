using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Usage.Commands;

/// <summary>Inserts a batch of <c>AiUsage</c> rows with one multi-row INSERT.</summary>
public sealed class RecordAiUsageHandler(ISession session)
{
    /// <summary>11 parameters a row: 500 rows stay far below PostgreSQL's 65 535 parameters.</summary>
    public const int MaxBatch = 500;

    public async Task Handle(RecordAiUsage command, CancellationToken ct)
    {
        foreach (var batch in command.Entries.Chunk(MaxBatch))
        {
            var values = string.Join(",", batch.Select((_, i) => $"(:at{i},:task{i},:kind{i},:op{i},:rev{i},:model{i},:items{i},:in{i},:out{i},:ms{i},:outcome{i})"));
            var query = session.CreateSQLQuery($"""
                INSERT INTO "AiUsage"("OccurredAt","Task","Kind","Operation","ProfileRevisionId","Model","Items","InputTokens","OutputTokens","LatencyMs","Outcome")
                VALUES {values}
                """);
            for (var i = 0; i < batch.Length; i++)
            {
                var entry = batch[i];
                query.SetParameter("at" + i, DateTime.SpecifyKind(entry.OccurredAt, DateTimeKind.Utc), NHibernateUtil.UtcDateTime)
                    .SetParameter("task" + i, entry.Task, NHibernateUtil.String)
                    .SetParameter("kind" + i, entry.Kind, NHibernateUtil.String)
                    .SetParameter("op" + i, entry.Operation, NHibernateUtil.String)
                    .SetParameter("rev" + i, entry.ProfileRevisionId, NHibernateUtil.Guid)
                    .SetParameter("model" + i, entry.Model, NHibernateUtil.String)
                    .SetParameter("items" + i, entry.Items, NHibernateUtil.Int32)
                    .SetParameter("in" + i, entry.InputTokens, NHibernateUtil.Int64)
                    .SetParameter("out" + i, entry.OutputTokens, NHibernateUtil.Int64)
                    .SetParameter("ms" + i, entry.LatencyMs, NHibernateUtil.Int32)
                    .SetParameter("outcome" + i, entry.Outcome, NHibernateUtil.String);
            }
            await query.ExecuteUpdateAsync(ct);
        }
    }
}
