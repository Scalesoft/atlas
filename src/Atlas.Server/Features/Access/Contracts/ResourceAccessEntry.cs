namespace Atlas.Server.Features.Access.Contracts;

/// <summary>A person or group with access to one resource. Level: the highest effective source; null when none of the sources is
/// effective now (a grant that has not started or has expired).</summary>
public sealed record ResourceAccessEntry(AccessSubject Subject, string? Level, IReadOnlyList<AccessEntrySource> Sources);
