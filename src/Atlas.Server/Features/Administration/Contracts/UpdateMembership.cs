namespace Atlas.Server.Features.Administration.Contracts;

public sealed record UpdateMembership(bool IsMember, string? ExpectedVersion);
