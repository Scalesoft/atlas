namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>One gesture: a client-chosen operation id (the retry key) and 1–200 changes, applied all or none.</summary>
public sealed record BoardBatchRequest(Guid? OperationId, IReadOnlyList<BoardChange>? Changes);
