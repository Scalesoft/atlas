namespace Atlas.Server.Features.PersonalApi.Contracts;

public sealed record ApiPageEntry(Guid Id, Guid SpaceId, Guid? ParentId, string Title, int Version, bool IsPublished);
