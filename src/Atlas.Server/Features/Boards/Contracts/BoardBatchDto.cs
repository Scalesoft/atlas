namespace Atlas.Server.Features.Boards.Contracts;

public sealed record BoardBatchDto(long Sequence, Guid OperationId, Guid AuthorId, IReadOnlyList<BoardChange> Changes);
