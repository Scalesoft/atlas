namespace Atlas.Server.Features.Boards.Commands;

/// <summary>A new board at the end of its siblings: under <paramref name="ParentPageId"/> or at the space root. With
/// <paramref name="Purpose"/> (only <see cref="Domain.Board.PagePurpose"/>) it is the page's own board instead, no node of the tree.</summary>
public sealed record CreateBoard(Guid SpaceId, string? Title, Guid? ParentPageId = null, string? Purpose = null);
