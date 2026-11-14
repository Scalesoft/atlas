using NHibernate.Criterion;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// Legacy resolved grants inside a single SQL statement, so no intermediate identifier set was ever materialised.
/// Splitting that into two queries round-trips identifiers back as an IN (...) list; chunking keeps every
/// parameter list bounded without dropping a single grant.
/// </summary>
internal static class GrantQueryLimits
{
    public static async Task<IReadOnlyDictionary<Guid, DateTime?>> GroupDeadlinesAsync(NHibernate.ISession session, Guid userId, CancellationToken ct)
    {
        var rows = await session.QueryOver<GroupMembership>().Where(m => m.UserId == userId)
            .Select(m => m.GroupId, m => m.VerifiedUntil).ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => (DateTime?)row[1]);
    }

    public static DateTime? Deadline(Guid? groupId, DateTime? grantEnd, IReadOnlyDictionary<Guid, DateTime?> groups)
    {
        if (groupId is not { } group) return grantEnd;
        if (!groups.TryGetValue(group, out var membershipEnd)) return DateTime.MinValue;
        return grantEnd is null ? membershipEnd : membershipEnd is null ? grantEnd : grantEnd < membershipEnd ? grantEnd : membershipEnd;
    }

    public const int ChunkSize = 1000;

    public static ICriterion InChunks(IProjection property, IList<Guid> values)
    {
        var batches = Restrictions.Disjunction();
        for (var offset = 0; offset < values.Count; offset += ChunkSize)
        {
            var batch = new object[Math.Min(ChunkSize, values.Count - offset)];
            for (var index = 0; index < batch.Length; index++)
            {
                batch[index] = values[offset + index];
            }
            batches.Add(Restrictions.In(property, batch));
        }
        return batches;
    }

    /// <summary>A SpaceGrant or ResourceGrant of the account itself or of one of its current groups, in the same statement.</summary>
    public static ICriterion OfAccount(Guid userId)
    {
        var groups = QueryOver.Of<GroupMembership>()
            .Where(m => m.UserId == userId && m.ActiveTo == null)
            .Select(m => m.GroupId);
        return Restrictions.Or(Restrictions.Eq("UserId", userId), Subqueries.PropertyIn("GroupId", groups.DetachedCriteria));
    }

    /// <summary>Whole-grant business validity at the database statement clock, exactly as the EffectiveSpaceGrant and EffectiveResourceGrant views filter it.</summary>
    public static ICriterion CurrentlyValid()
    {
        return Expression.Sql("({alias}.\"StartAt\" IS NULL OR {alias}.\"StartAt\" <= statement_timestamp()) AND ({alias}.\"ExpiresAt\" IS NULL OR statement_timestamp() < {alias}.\"ExpiresAt\")");
    }
}
