namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>The handler's answer; the endpoint sends the array itself. A wrapper, because Wolverine would cascade the
/// elements of a returned enumerable as messages.</summary>
public sealed record BoardList(IReadOnlyList<BoardSummaryDto> Boards);
