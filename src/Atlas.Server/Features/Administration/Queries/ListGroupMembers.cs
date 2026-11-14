namespace Atlas.Server.Features.Administration.Queries;

public sealed record ListGroupMembers(Guid GroupId, string? Search = null, int Offset = 0);
