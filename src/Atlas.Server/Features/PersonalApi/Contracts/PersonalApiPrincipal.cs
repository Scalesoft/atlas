namespace Atlas.Server.Features.PersonalApi.Contracts;

/// <summary>What the bearer scheme needs from an accepted credential. Never an entity: the authenticating session is closed before the claims are built.</summary>
/// <param name="Scopes">The effective scopes: the token's scopes within the owner's current level.</param>
/// <param name="GrantedScopes">The token's own scopes, only to tell a missing scope from one the ceiling removed.</param>
/// <param name="Channel">The channel the token was accepted on (rest, mcp).</param>
public sealed record PersonalApiPrincipal(Guid ActorId, string Username, string SecurityStamp, Guid TokenId, string[] Scopes,
    string[] GrantedScopes, string Channel);
