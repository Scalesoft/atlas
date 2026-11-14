using Atlas.Server.Features.PageOrganization.Contracts;

namespace Atlas.Server.Features.Boards.Commands;

/// <summary>A board's place in the space tree, as a page's move (MovePage): a parent page or the root, before a sibling of any
/// type or at the end, under the tree's expected version.</summary>
public sealed record MoveBoard(Guid BoardId, MovePageRequest Request);

/// <summary>A board back from the space's trash, as a page's restore (UntrashPage): under an active parent or at the root.</summary>
public sealed record UntrashBoard(Guid BoardId, UntrashPageRequest Request);
