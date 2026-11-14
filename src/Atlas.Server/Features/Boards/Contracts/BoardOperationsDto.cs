namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>The batches after a sequence, in order, and the board's current sequence.</summary>
public sealed record BoardOperationsDto(long Sequence, IReadOnlyList<BoardBatchDto> Batches);
