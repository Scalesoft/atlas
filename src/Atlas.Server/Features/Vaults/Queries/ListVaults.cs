namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>Current vault registrations the actor manages (all for WikiAdmin, otherwise those of spaces the actor administers),
/// each with the actor's own vault level.</summary>
public sealed record ListVaults;
