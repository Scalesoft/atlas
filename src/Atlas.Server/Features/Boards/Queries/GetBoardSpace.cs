namespace Atlas.Server.Features.Boards.Queries;

/// <summary>The space of a board its account may read (a Reader of the space; a trashed board is 404), for the host services that
/// work per space, like YouTrack's for the editor's youtrack block.</summary>
public sealed record GetBoardSpace(Guid BoardId);

/// <summary>A wrapper, so the answer is not mistaken for a cascading message.</summary>
public sealed record BoardSpace(Guid SpaceId);
