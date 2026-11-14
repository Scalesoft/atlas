using Atlas.YouTrack;

namespace Atlas.Server.Features.YouTrack.Contracts;

/// <summary>A handler may not answer with a bare null; the miss is modelled explicitly.</summary>
public sealed record YouTrackIssueResult(YouTrackIssue? Issue);
