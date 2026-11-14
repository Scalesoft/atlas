namespace Atlas.Server.Features.PageOrganization.Contracts;

public sealed record MovePageRequest(Guid? ParentId, Guid? BeforePageId, long ExpectedTreeVersion);
