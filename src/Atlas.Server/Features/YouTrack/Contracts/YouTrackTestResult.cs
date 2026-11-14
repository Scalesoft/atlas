using Atlas.YouTrack;

namespace Atlas.Server.Features.YouTrack.Contracts;

public sealed record YouTrackTestResult(IReadOnlyList<YouTrackProject> Projects);
