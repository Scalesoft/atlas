namespace Atlas.Server.Features.PageOrganization.Contracts;

public sealed record TrashPageRequest(bool IncludeSubtree, long ExpectedTreeVersion, int ExpectedVersion);
