namespace Atlas.Server.Features.PageOrganization.Contracts;

/// <param name="ParentId">The page whose direct children are sorted; null: the space root.</param>
public sealed record SortTreeLevelRequest(Guid? ParentId, bool ByTitle);
