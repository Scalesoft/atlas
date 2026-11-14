namespace Atlas.Server.Features.SearchGaps.Commands;

/// <summary>Zero-result queries the recorder committed. Sent by SearchMissFlusher in a system scope, never over HTTP.</summary>
public sealed record RecordSearchMisses(IReadOnlyList<SearchMiss> Misses);

/// <summary>One committed miss. The user id lives only in memory; RecordSearchMissesHandler hashes it at once. SpaceId is the
/// search filter or the omnisearch context and is kept only when the user reaches that space.</summary>
public sealed record SearchMiss(Guid UserId, Guid? SpaceId, string Normalized, string Display, DateOnly On,
    string Reason = Contracts.SearchGapReason.Zero);
