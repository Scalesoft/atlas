namespace Atlas.Server.Features.PageOrganization.Contracts;

public sealed record PageTrashDto(Guid SpaceId, long Version, IReadOnlyList<TrashedPageItem> Items, bool HasMore);
