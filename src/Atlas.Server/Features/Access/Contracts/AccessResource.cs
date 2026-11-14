namespace Atlas.Server.Features.Access.Contracts;

/// <summary>One resource of a kind: the "K čemu" search result and the header of a resource's access list.</summary>
public sealed record AccessResource(string Kind, string Id, string Title, string? ParentTitle = null, Guid? SpaceId = null,
    string? SpaceName = null, string? Note = null);
