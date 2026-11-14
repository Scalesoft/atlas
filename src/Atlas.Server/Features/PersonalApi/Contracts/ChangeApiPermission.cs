namespace Atlas.Server.Features.PersonalApi.Contracts;

/// <param name="CanIssueTokens">The 1.5 switch, for clients that do not send Level: false is level none; true enables level write, or
/// keeps the current level when the account already has one.</param>
/// <param name="Level">none, read or write; takes precedence over CanIssueTokens.</param>
public sealed record ChangeApiPermission(bool? CanIssueTokens, string? ExpectedVersion, string? Level = null);
