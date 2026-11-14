namespace Atlas.Server.Features.PersonalApi.Contracts;

public sealed record ApiTokenProfile(bool CanIssueTokens, string? PermissionVersion, IReadOnlyList<ApiTokenDto> Tokens,
    string ApiBaseUrl = "/api/v1", string OpenApiUrl = "/api/profile/api-document")
{
    /// <summary>The owner's ceiling: none, read or write. New tokens may hold only the scopes of this level.</summary>
    public string Level { get; init; } = ApiLevels.None;
    /// <summary>Whether the MCP module is enabled (filled by the MCP module; false until then).</summary>
    public bool McpEnabled { get; init; }
    /// <summary>The MCP endpoint when the module is enabled.</summary>
    public string? McpUrl { get; init; }
}
