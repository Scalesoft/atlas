namespace Atlas.Server.Features.PersonalApi.Contracts;

/// <param name="Scopes">read, optionally write; all within the owner's level. A former scope (draft, publish, direct) is refused with 400.</param>
/// <param name="Channels">rest and/or mcp; null is ["rest"], as before channels existed.</param>
public sealed record CreateApiToken(string Name, string[] Scopes, int ExpiresInDays = 90, string[]? Channels = null);
