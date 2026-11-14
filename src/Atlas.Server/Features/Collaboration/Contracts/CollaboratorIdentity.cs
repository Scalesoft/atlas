namespace Atlas.Server.Features.Collaboration.Contracts;

/// <summary>What the relay needs about the handshake account. A contract, so no entity escapes the message scope.</summary>
public sealed record CollaboratorIdentity(Guid Id, string DisplayName, string SecurityStamp, Guid? SessionId = null);
