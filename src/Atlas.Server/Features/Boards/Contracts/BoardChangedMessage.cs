namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>BoardChanged on the channel <c>board:{id}</c> of /hubs/live, sent only after the batch committed.</summary>
public sealed record BoardChangedMessage(Guid BoardId, long Sequence, Guid OperationId, Guid AuthorId, IReadOnlyList<BoardChange> Changes);
