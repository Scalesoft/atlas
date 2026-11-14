using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Auth.Contracts;

/// <summary>Features of the signed-in user. EditorAssist is the wiki-level switch (module "Návrhy při psaní") narrowed by the user's own
/// choice (G4: "off" turns it off; "rules" behaves as "on" until phase 4). DisabledSuggestionTypes is absent when the user disabled none.</summary>
public sealed record UserDto(Guid Id, string Username, string DisplayName, bool IsWikiAdmin, Appearance Appearance, UserFeatures Features)
{
    /// <summary>The release whose notes („Co je nového“) the account has seen; null for a new account, which takes the running one silently.</summary>
    public string? ReleaseNotesSeen { get; init; }
}

public sealed record UserFeatures(
    bool EditorAssist,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? DisabledSuggestionTypes = null)
{
    /// <summary>The MCP server module is enabled (Space settings show "AI a znalosti" for it too, D10). A flag only, never the URL.</summary>
    public bool Mcp { get; init; }
}
