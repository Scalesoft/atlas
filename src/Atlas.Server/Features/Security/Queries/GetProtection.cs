namespace Atlas.Server.Features.Security.Queries;

/// <summary>The protection of the instance (TargetId null), a vault or a secret. Level: instance, vault or secret.</summary>
public sealed record GetProtection(string Level, Guid? TargetId);
