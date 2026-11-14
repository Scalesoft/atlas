namespace Atlas.Server.Features.TreeReferences.Contracts;

/// <param name="TargetId">Any object the account may read (a page, a board, a contact…), also of another space or the wiki.</param>
/// <param name="Anchor">A place inside the target: a heading id or a block id.</param>
/// <param name="ParentId">The page the node goes under; null: the space root.</param>
/// <param name="BeforeId">The sibling (a page, a board or a node) it goes before; null: the end of the level.</param>
public sealed record AddTreeReferenceRequest(Guid TargetId, string? Anchor = null, Guid? ParentId = null, Guid? BeforeId = null);
