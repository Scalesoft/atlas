namespace Atlas.Server.Features.PageHistory.Contracts;

/// <summary>The personal API token behind a change, publication or opening. Only its owner and wiki administrators receive it
/// (ApiTokenVisibility); everyone else gets the channel alone. Revoked: the token no longer works, its name stays.</summary>
public sealed record ApiTokenRef(Guid Id, string Name, bool Revoked);
