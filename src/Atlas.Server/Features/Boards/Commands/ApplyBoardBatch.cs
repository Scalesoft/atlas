using Atlas.Server.Features.Boards.Contracts;

namespace Atlas.Server.Features.Boards.Commands;

public sealed record ApplyBoardBatch(Guid BoardId, Guid? OperationId, IReadOnlyList<BoardChange>? Changes);
