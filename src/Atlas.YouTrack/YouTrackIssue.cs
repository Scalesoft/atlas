namespace Atlas.YouTrack;

public sealed record YouTrackIssue(string Id, string Key, string Title, string Url, string? State = null, string? DueDate = null);
