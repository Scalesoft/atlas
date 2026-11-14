namespace Atlas.Server.Features.Spaces.Contracts;

public sealed record SpacePageListItem(Guid Id, Guid SpaceId, Guid? ParentId, string Title, int Version, bool IsPublished);
