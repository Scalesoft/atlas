namespace Atlas.Server.Features.PageOrganization.Contracts;

public sealed record CrossSpaceChange(Guid SourceSpaceId, Guid TargetSpaceId, long SourceVersion, long TargetVersion, IReadOnlyList<Guid> AffectedIds);
