namespace Atlas.Server.Features.Boards.Domain;

/// <summary>A board as BoardAccess opened it in this transaction: the row under its lock, the account under its lock, whether it
/// may write, and the pages of interest with the spaces locked for them.</summary>
public sealed record OpenedBoard(Board Board, UserAccount Actor, bool CanEdit, IReadOnlyDictionary<Guid, Guid> PageSpaces, IReadOnlySet<Guid> LockedSpaces);
