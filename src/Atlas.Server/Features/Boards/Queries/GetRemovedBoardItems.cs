namespace Atlas.Server.Features.Boards.Queries;

/// <summary>The items taken off a board that a Writer can still restore with a <c>restore</c> change (newest first, at most
/// <see cref="GetRemovedBoardItemsHandler.Limit"/>): what the board's „Odebrané“ lists.</summary>
public sealed record GetRemovedBoardItems(Guid BoardId);

/// <summary>One removed item: its kind, and its text (cut) or, for a page card, the title the reader may see.</summary>
public sealed record RemovedBoardItemDto(Guid Id, string Kind, string? Text);

public sealed record RemovedBoardItemsDto(IReadOnlyList<RemovedBoardItemDto> Items);
