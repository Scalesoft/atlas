using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using NHibernate;
using Atlas.Server.Features.SearchGaps.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SearchGaps;

/// <summary>A row of the search query log: no identity, only the keyed hash of user, space and query.</summary>
public sealed record SearchQueryLogRow(Guid? SpaceId, string NormalizedQuery, string DisplayQuery, byte[] UserKey, DateOnly OccurredOn,
    string Reason = SearchGapReason.Zero);

/// <summary>SQL of the search query log, its key and the gap resolutions (migration 2026100440).</summary>
public sealed class SearchGapStore(ISession session)
{
    private const int InsertBatch = 100;

    /// <summary>The HMAC secret: 32 random bytes stored only data-protected, created on first use. When the keyring can no longer
    /// unprotect it (keys lost), a new secret replaces it; people then count anew, which a statistic tolerates.</summary>
    public async Task<(byte[] Secret, bool Replaced)> SecretAsync(IDataProtector protector, CancellationToken ct)
    {
        var stored = await ReadSecretAsync(ct);
        if (stored is null)
        {
            await session.CreateSQLQuery("""INSERT INTO "SearchQueryLogKey"("Id","ProtectedKey") VALUES (1,:key) ON CONFLICT ("Id") DO NOTHING""")
                .SetParameter("key", protector.Protect(RandomNumberGenerator.GetBytes(32)), NHibernateUtil.BinaryBlob).ExecuteUpdateAsync(ct);
            stored = await ReadSecretAsync(ct);
        }
        try
        {
            return (protector.Unprotect(stored!), false);
        }
        catch (CryptographicException)
        {
            var secret = RandomNumberGenerator.GetBytes(32);
            await session.CreateSQLQuery("""UPDATE "SearchQueryLogKey" SET "ProtectedKey"=:key WHERE "Id"=1""")
                .SetParameter("key", protector.Protect(secret), NHibernateUtil.BinaryBlob).ExecuteUpdateAsync(ct);
            return (secret, true);
        }
    }

    public async Task InsertAsync(IReadOnlyList<SearchQueryLogRow> rows, CancellationToken ct)
    {
        // One statement may not touch a row twice: one key and day collapses to its strongest reason first.
        var unique = rows.GroupBy(row => (Convert.ToBase64String(row.UserKey), row.OccurredOn))
            .Select(group => group.OrderByDescending(row => SearchGapReason.Strength(row.Reason)).First()).ToArray();
        foreach (var batch in unique.Chunk(InsertBatch))
        {
            var values = string.Join(",", batch.Select((_, i) => $"(:s{i},:n{i},:d{i},:k{i},:o{i},:r{i})"));
            // A later and stronger reason of the same person, query and day replaces the stored one; a weaker one never does.
            var query = session.CreateSQLQuery($"""
                INSERT INTO "SearchQueryLog"("SpaceId","NormalizedQuery","DisplayQuery","UserKey","OccurredOn","Reason") VALUES {values}
                ON CONFLICT ("UserKey","OccurredOn") DO UPDATE SET "Reason"=excluded."Reason"
                WHERE (CASE excluded."Reason" WHEN 'reported' THEN 2 WHEN 'no_answer' THEN 1 ELSE 0 END)
                    > (CASE "SearchQueryLog"."Reason" WHEN 'reported' THEN 2 WHEN 'no_answer' THEN 1 ELSE 0 END)
                """);
            for (var i = 0; i < batch.Length; i++)
            {
                query.SetParameter("s" + i, batch[i].SpaceId, NHibernateUtil.Guid)
                    .SetParameter("n" + i, batch[i].NormalizedQuery)
                    .SetParameter("d" + i, batch[i].DisplayQuery)
                    .SetParameter("k" + i, batch[i].UserKey, NHibernateUtil.BinaryBlob)
                    .SetParameter("o" + i, batch[i].OccurredOn.ToDateTime(TimeOnly.MinValue), NHibernateUtil.Date)
                    .SetParameter("r" + i, batch[i].Reason);
            }
            await query.ExecuteUpdateAsync(ct);
        }
    }

    /// <summary>Gaps of one space since a day (exclusive): only days after the gap's resolution day count, and a gap needs
    /// <see cref="SearchGapList.Threshold"/> distinct people. The shown text is the most frequent typed form.</summary>
    public async Task<IReadOnlyList<SearchGapItem>> ListAsync(Guid spaceId, DateOnly since, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
            SELECT mode() WITHIN GROUP (ORDER BY l."DisplayQuery") AS display,l."NormalizedQuery" AS normalized,
              CAST(count(DISTINCT l."UserKey") AS integer) AS user_count,CAST(max(l."OccurredOn") AS timestamp) AS last_on,
              CAST(count(DISTINCT l."UserKey") FILTER (WHERE l."Reason"='reported') AS integer) AS reported,
              CAST(count(DISTINCT l."UserKey") FILTER (WHERE l."Reason"='no_answer') AS integer) AS unanswered
            FROM "SearchQueryLog" l
            LEFT JOIN "SearchGapResolution" r ON r."SpaceId"=l."SpaceId" AND r."NormalizedQuery"=l."NormalizedQuery"
            WHERE l."SpaceId"=:space AND l."OccurredOn">:since
              AND (r."ResolvedAt" IS NULL OR l."OccurredOn">CAST(r."ResolvedAt" AT TIME ZONE 'UTC' AS date))
            GROUP BY l."NormalizedQuery"
            HAVING count(DISTINCT l."UserKey")>=:threshold
            ORDER BY user_count DESC,last_on DESC,normalized
            LIMIT :take
            """).AddScalar("display", NHibernateUtil.String).AddScalar("normalized", NHibernateUtil.String)
            .AddScalar("user_count", NHibernateUtil.Int32).AddScalar("last_on", NHibernateUtil.DateTime)
            .AddScalar("reported", NHibernateUtil.Int32).AddScalar("unanswered", NHibernateUtil.Int32)
            .SetParameter("space", spaceId).SetParameter("since", since.ToDateTime(TimeOnly.MinValue), NHibernateUtil.Date)
            .SetParameter("threshold", SearchGapList.Threshold).SetParameter("take", SearchGapList.MaximumItems)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new SearchGapItem((string)row[0], (string)row[1], (int)row[2], DateOnly.FromDateTime((DateTime)row[3]))
        {
            Reported = (int)row[4],
            Unanswered = (int)row[5],
        }).ToArray();
    }

    public async Task ResolveAsync(Guid spaceId, string normalizedQuery, Guid actorId, CancellationToken ct)
    {
        await session.CreateSQLQuery("""
            INSERT INTO "SearchGapResolution"("SpaceId","NormalizedQuery","ResolvedAt","ResolvedById") VALUES (:space,:query,now(),:actor)
            ON CONFLICT ("SpaceId","NormalizedQuery") DO UPDATE SET "ResolvedAt"=excluded."ResolvedAt","ResolvedById"=excluded."ResolvedById"
            """).SetParameter("space", spaceId).SetParameter("query", normalizedQuery).SetParameter("actor", actorId).ExecuteUpdateAsync(ct);
    }

    private async Task<byte[]?> ReadSecretAsync(CancellationToken ct)
        => await session.CreateSQLQuery("""SELECT "ProtectedKey" FROM "SearchQueryLogKey" WHERE "Id"=1""")
            .AddScalar("ProtectedKey", NHibernateUtil.BinaryBlob).UniqueResultAsync<byte[]?>(ct);
}
