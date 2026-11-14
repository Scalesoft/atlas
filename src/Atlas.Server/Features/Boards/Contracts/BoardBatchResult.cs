namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>The committed sequence of the batch; Duplicate when the same operation had already been applied.</summary>
public sealed record BoardBatchResult(long Sequence, bool Duplicate);
