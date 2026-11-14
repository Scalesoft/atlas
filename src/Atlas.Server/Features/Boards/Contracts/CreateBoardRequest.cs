namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>A board's title and, for one placed in the tree under a page, that page (none: the space root); with Purpose
/// <c>projector.board</c> the page's own board instead (CreateBoard).</summary>
public sealed record CreateBoardRequest(string? Title, Guid? ParentPageId = null, string? Purpose = null);
