namespace Atlas.Server.Features.Collaboration.Queries;

public sealed record AuthorizeCollaborator(Guid PageId, Guid Generation, Guid UserId, Guid? SessionId = null);
