namespace Atlas.Server.Features.PageOrganization.Contracts;

public sealed record MovePageSpaceRequest(Guid TargetSpaceId, Guid? ParentId, Guid? BeforePageId, bool IncludeSubtree,
    long ExpectedSourceTreeVersion, long ExpectedTargetTreeVersion, int ExpectedVersion, bool AcknowledgeAccessChange);
