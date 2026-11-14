namespace Atlas.Server.Features.Pages.Queries;

public sealed record GetSpaceDrafts(Guid SpaceId, string? Query = null, Guid? AuthorId = null,
    string Kind = "all", int Offset = 0, int Limit = 100, Guid? PageId = null);
