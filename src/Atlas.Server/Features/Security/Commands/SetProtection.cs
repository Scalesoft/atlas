using Atlas.Server.Features.Security.Contracts;

namespace Atlas.Server.Features.Security.Commands;

/// <summary>Sets (or, with a null mode, clears to inherit) the protection mode of the instance (TargetId null), a vault or a secret.</summary>
public sealed record SetProtection(string Level, Guid? TargetId, UpdateProtection Request);
