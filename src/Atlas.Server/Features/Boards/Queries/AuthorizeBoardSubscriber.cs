namespace Atlas.Server.Features.Boards.Queries;

/// <summary>A live subscriber of a board, decided for the account its connection authenticated with (not a request actor).</summary>
public sealed record AuthorizeBoardSubscriber(Guid BoardId, Guid UserId, string SecurityStamp, Guid? SessionId);
