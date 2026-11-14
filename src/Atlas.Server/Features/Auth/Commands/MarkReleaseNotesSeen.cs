namespace Atlas.Server.Features.Auth.Commands;

/// <summary>The account has seen the notes of this release („Co je nového“): the notice of it is not shown again, on any device.</summary>
public sealed record MarkReleaseNotesSeen(string Version);

public sealed record ReleaseNotesSeenRequest(string Version);
