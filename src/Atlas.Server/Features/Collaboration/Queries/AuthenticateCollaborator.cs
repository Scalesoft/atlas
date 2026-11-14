namespace Atlas.Server.Features.Collaboration.Queries;

public sealed record AuthenticateCollaborator(Guid UserId, string? Stamp, Guid? SessionId = null);
