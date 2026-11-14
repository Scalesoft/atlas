namespace Atlas.Server.Features.PersonalApi.Queries;

public sealed record ListApiSpacePages(Guid SpaceId, int? Offset, int? Limit);
