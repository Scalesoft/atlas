namespace Atlas.Server.Features.Access.Contracts;

/// <summary>The access card of a person or group. Scope: wiki (every resource), space (one space, for its Admin) or self (the
/// caller's own card). CanManage: the caller may change the grants shown.</summary>
public sealed record PrincipalAccess(AccessPrincipalView Principal, string Scope, IReadOnlyList<AccessEntry> Entries, bool CanManage);
