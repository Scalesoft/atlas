namespace Atlas.Server.Features.PersonalApi.Contracts;

/// <param name="CanIssueTokens">Level is not none.</param>
public sealed record ApiPermissionDto(bool CanIssueTokens, string? Version)
{
    /// <summary>none, read or write.</summary>
    public string Level { get; init; } = ApiLevels.None;
}
