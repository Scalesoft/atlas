using NHibernate;
using Atlas.Server.Features.AccessAudit.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AccessAudit.Queries;

public sealed class GetActivitySummaryHandler(ISession session, IContentAccess access, TimeProvider clock)
{
    // Outcomes that are not failures: success, and the assistant proposal states (AiProposalStore).
    private const string NotFailures = "'ok','pending','applied','declined','expired','superseded'";

    public async Task<ActivitySummary> Handle(GetActivitySummary query, CancellationToken ct)
    {
        if (query.Days is < 1 or > 3650)
        {
            throw new ApiException(400, "Období lze zadat na 1 až 3650 dní.");
        }
        var actor = await access.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(session, actor, ct))
        {
            throw new ApiException(403, "Přehled aktivity je dostupný pouze správci wiki.");
        }
        DateTime? from = query.Days is { } days ? clock.GetUtcNow().UtcDateTime.AddDays(-days) : null;
        string Since(string column) => from is null ? "TRUE" : $"{column} >= :since";

        var revisionChannels = await Counts($"""SELECT "Channel" AS k, count(*) AS n FROM "PageRevision" WHERE {Since("\"ActiveFrom\"")} GROUP BY "Channel" ORDER BY n DESC, k""");
        var opens = await Counts($"""SELECT "Channel" AS k, count(*) AS n FROM "PageAccessEvent" WHERE "Action" = 'page.opened' AND "Channel" IS NOT NULL AND {Since("\"OccurredAt\"")} GROUP BY "Channel" ORDER BY n DESC, k""");
        var toolChannels = await Counts($"""SELECT "Channel" AS k, count(*) AS n FROM "ApiActivity" WHERE {Since("\"OccurredAt\"")} GROUP BY "Channel" ORDER BY n DESC, k""");
        var aiAssisted = await Count($"""SELECT count(*) AS n FROM "PageRevision" WHERE "AiAssisted" AND {Since("\"ActiveFrom\"")}""");
        var failures = await Count($"""SELECT count(*) AS n FROM "ApiActivity" WHERE "Outcome" NOT IN ({NotFailures}) AND {Since("\"OccurredAt\"")}""");

        var editors = await People($"""
            SELECT r."AuthorId" AS id, COALESCE(u."DisplayName", 'Neznámý účet') AS name, count(*) AS n
            FROM "PageRevision" r LEFT JOIN "UserAccount" u ON u."Id" = r."AuthorId"
            WHERE {Since("r.\"ActiveFrom\"")} GROUP BY r."AuthorId", u."DisplayName" ORDER BY n DESC, name LIMIT 5
            """);
        var users = await People($"""
            SELECT a."ActorId" AS id, COALESCE(u."DisplayName", 'Neznámý účet') AS name, count(*) AS n
            FROM "ApiActivity" a LEFT JOIN "UserAccount" u ON u."Id" = a."ActorId"
            WHERE {Since("a.\"OccurredAt\"")} GROUP BY a."ActorId", u."DisplayName" ORDER BY n DESC, name LIMIT 5
            """);
        var tools = (await Rows($"""
            SELECT "Channel" AS channel, "Operation" AS operation, count(*) AS calls,
                   count(*) FILTER (WHERE "Outcome" NOT IN ({NotFailures})) AS failures
            FROM "ApiActivity" WHERE {Since("\"OccurredAt\"")} GROUP BY "Channel", "Operation"
            ORDER BY calls DESC, "Channel", "Operation" LIMIT 8
            """, ("channel", NHibernateUtil.String), ("operation", NHibernateUtil.String), ("calls", NHibernateUtil.Int64), ("failures", NHibernateUtil.Int64)))
            .Select(x => new ActivityTool((string)x[0], (string)x[1], Clamp(x[2]), Clamp(x[3]))).ToArray();

        return new ActivitySummary(from, Total(revisionChannels), aiAssisted, revisionChannels, editors, opens, Total(toolChannels), failures,
            toolChannels, tools, users);

        static int Clamp(object value) => (int)Math.Min((long)value, int.MaxValue);
        static int Total(ActivityCount[] counts) => (int)Math.Min(counts.Sum(x => (long)x.Count), int.MaxValue);

        async Task<List<object[]>> Rows(string sql, params (string Name, NHibernate.Type.IType Type)[] columns)
        {
            var q = session.CreateSQLQuery(sql);
            foreach (var (name, type) in columns)
            {
                q.AddScalar(name, type);
            }
            if (from is { } since)
            {
                q.SetParameter("since", since, NHibernateUtil.UtcDateTime);
            }
            return [.. await q.ListAsync<object[]>(ct)];
        }

        async Task<int> Count(string sql)
        {
            var q = session.CreateSQLQuery(sql).AddScalar("n", NHibernateUtil.Int64);
            if (from is { } since)
            {
                q.SetParameter("since", since, NHibernateUtil.UtcDateTime);
            }
            return Clamp(await q.UniqueResultAsync<object>(ct));
        }

        async Task<ActivityCount[]> Counts(string sql)
            => (await Rows(sql, ("k", NHibernateUtil.String), ("n", NHibernateUtil.Int64))).Select(x => new ActivityCount((string)x[0], Clamp(x[1]))).ToArray();

        async Task<ActivityPerson[]> People(string sql)
            => (await Rows(sql, ("id", NHibernateUtil.Guid), ("name", NHibernateUtil.String), ("n", NHibernateUtil.Int64)))
                .Select(x => new ActivityPerson((Guid)x[0], (string)x[1], Clamp(x[2]))).ToArray();
    }
}
