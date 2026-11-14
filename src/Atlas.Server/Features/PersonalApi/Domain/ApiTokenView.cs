using Atlas.Server.Features.PersonalApi.Contracts;

namespace Atlas.Server.Features.PersonalApi.Domain;

/// <summary>The wire shape of credential metadata, with scopes and channels in canonical order. Both the profile query and the
/// issuing command project through it, so neither has to reach into the other's handler.</summary>
public static class ApiTokenView
{
    /// <param name="level">The owner's current level; the effective scopes are the token's within it.</param>
    /// <param name="owner">The owner as currently stored: an inactive account or a rotated stamp makes every token unusable.</param>
    /// <param name="mcpEnabled">Whether the MCP module is enabled; until it is, the channel mcp makes nothing usable.</param>
    public static ApiTokenDto ToDto(PersonalApiToken token, string level, UserAccount owner, DateTime now, bool mcpEnabled = false)
    {
        var effective = ApiLevels.Effective(token.Scopes, level);
        var channels = ApiChannels.Ordered(token.Channels);
        var open = token.ActiveTo is null && token.ExpiresAt > now && owner.IsActive && token.SecurityStamp == owner.SecurityStamp;
        var reachable = channels.Any(channel => channel == ApiChannels.Rest || channel == ApiChannels.Mcp && mcpEnabled);
        return new ApiTokenDto(token.Id, token.Name, token.Prefix, ApiScopes.Ordered(token.Scopes), token.ActiveFrom, token.ExpiresAt, token.ActiveTo)
        {
            Channels = channels,
            EffectiveScopes = effective,
            Usable = open && effective.Length > 0 && reachable
        };
    }
}
