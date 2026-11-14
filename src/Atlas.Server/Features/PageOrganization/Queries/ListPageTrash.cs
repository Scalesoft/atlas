namespace Atlas.Server.Features.PageOrganization.Queries;

public sealed record ListPageTrash(Guid SpaceId, int Offset, int Limit);
