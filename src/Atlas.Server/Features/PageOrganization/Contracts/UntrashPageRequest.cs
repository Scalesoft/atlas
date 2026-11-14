namespace Atlas.Server.Features.PageOrganization.Contracts;

public sealed record UntrashPageRequest(Guid? ParentId, bool IncludeSubtree, long ExpectedTreeVersion);
