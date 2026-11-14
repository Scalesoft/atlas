namespace Atlas.Server.Features.PersonalApi.Contracts;

/// <param name="Scopes">The token's own scopes (read, write) in that order.</param>
public sealed record ApiTokenDto(Guid Id, string Name, string Prefix, string[] Scopes, DateTime ActiveFrom, DateTime ExpiresAt, DateTime? ActiveTo)
{
    /// <summary>rest and/or mcp.</summary>
    public string[] Channels { get; init; } = [ApiChannels.Rest];
    /// <summary>The scopes within the owner's current level: what the token may do on its next request.</summary>
    public string[] EffectiveScopes { get; init; } = [];
    /// <summary>Open, not expired, issued under the current session stamp, a level other than none, and a channel that is available
    /// (rest always; mcp once the MCP module is enabled).</summary>
    public bool Usable { get; init; }
}
