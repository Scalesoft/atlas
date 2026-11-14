using Atlas.YouTrack;

namespace Atlas.Server.Features.YouTrack.Contracts;

/// <summary>Wolverine treats a bare collection return value as cascaded messages, so the list travels inside a record. The endpoint still writes a plain JSON array.</summary>
public sealed record YouTrackIssueList(IReadOnlyList<YouTrackIssue> Items);
